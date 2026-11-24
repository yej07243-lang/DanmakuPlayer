package app.danmaku.player.player

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.text.TextPaint
import android.text.TextUtils
import android.util.TypedValue
import android.view.View
import app.danmaku.player.Danmaku
import kotlin.math.min

data class DanmakuStyle(
    val enabled: Boolean = true,
    val font: Float = 20f,
    val opacity: Float = 0.9f,
    val area: Float = 0.65f,
    val speed: Float = 1f,
    val scrolling: Boolean = true,
    val top: Boolean = true,
    val bottom: Boolean = true,
    val maxVisible: Int = 60,
    val blockedWords: List<String> = emptyList(),
)

class DanmakuOverlay(context: Context) : View(context) {
    private data class Cue(val item: Danmaku, val text: String, val width: Float, val y: Float, val lifetime: Double)

    private val paint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply {
        typeface = Typeface.create("sans-serif", Typeface.BOLD)
        strokeJoin = Paint.Join.ROUND
    }
    private val video = RectF()
    private var cues = emptyList<Cue>()
    private var entries = emptyList<Danmaku>()
    private var aspect = 0.0
    private var time = Double.NaN
    var style = DanmakuStyle()
        set(value) {
            if (field == value) return
            field = value
            layoutComments()
        }

    fun setComments(entries: List<Danmaku>) {
        this.entries = entries
        layoutComments()
    }

    fun update(time: Double, aspect: Double) {
        if (this.aspect != aspect) {
            this.aspect = aspect
            layoutComments()
        }
        if (this.time == time) return
        this.time = time
        if (style.enabled && cues.isNotEmpty()) postInvalidateOnAnimation()
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        layoutComments()
    }

    private fun layoutComments() {
        val ratio = if (aspect > 0) aspect.toFloat() else 16f / 9f
        val videoWidth = min(width.toFloat(), height * ratio)
        val videoHeight = videoWidth / ratio
        video.set((width - videoWidth) / 2f, (height - videoHeight) / 2f,
            (width + videoWidth) / 2f, (height + videoHeight) / 2f)
        cues = if (style.enabled && videoWidth > 0 && videoHeight > 0) placeComments() else emptyList()
        postInvalidateOnAnimation()
    }

    private fun placeComments(): List<Cue> {
        paint.textSize = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, style.font, resources.displayMetrics)
            .coerceAtMost(video.height() / 4)
        val line = paint.textSize * 1.25f
        paint.strokeWidth = paint.textSize * 0.09f
        val pad = paint.textSize * 0.25f
        val lanes = ((video.height() * style.area - pad) / line).toInt().coerceIn(1, 24)
        val topFixed = if (style.top) min(lanes / 3, 3).coerceAtLeast(1) else 0
        val bottomFixed = if (style.bottom) min(lanes / 3, 3).coerceAtLeast(1) else 0
        val top = arrayOfNulls<Cue>(topFixed)
        val bottom = arrayOfNulls<Cue>(bottomFixed)
        val spaceForLanes = ((video.height() - pad * 2) / line).toInt()
        val scrollLanes = min(lanes, spaceForLanes - bottomFixed).coerceAtLeast(1)
        val scroll = arrayOfNulls<Cue>((scrollLanes - topFixed).coerceAtLeast(1))
        val lifetime = 8.0 / style.speed
        val maximumStarts = (style.maxVisible / maxOf(4.0, lifetime)).toInt().coerceAtLeast(1)
        val finishes = java.util.PriorityQueue<Double>()
        var bucket = -1L
        var started = 0
        return buildList {
            for (item in entries) {
                if (style.blockedWords.any { item.text.contains(it, ignoreCase = true) }) continue
                while (finishes.peek()?.let { it <= item.time } == true) finishes.poll()
                val second = item.time.toLong()
                if (second != bucket) {
                    bucket = second
                    started = 0
                }
                if (finishes.size >= style.maxVisible || started >= maximumStarts) continue
                val group = when (item.mode) {
                    4 -> bottom
                    5 -> top
                    else -> if (style.scrolling) scroll else continue
                }
                if (group.isEmpty()) continue
                val text = if (group === scroll) item.text else
                    TextUtils.ellipsize(item.text, paint, video.width() - pad * 2, TextUtils.TruncateAt.END).toString()
                val textWidth = paint.measureText(text)
                val lane = group.indexOfFirst { previous ->
                    previous == null || item.time >= previous.item.time + previous.lifetime ||
                        (group === scroll && canFollow(previous, item, textWidth))
                }
                if (lane < 0) continue
                val y = when {
                    group === bottom -> video.bottom - pad - line * lane
                    group === top -> video.top + pad + line * (lane + 1)
                    else -> video.top + pad + line * (topFixed + lane + 1)
                }
                val cue = Cue(item, text, textWidth, y, if (group === scroll) lifetime else 4.0)
                group[lane] = cue
                finishes.add(item.time + cue.lifetime)
                started++
                add(cue)
            }
        }
    }

    private fun canFollow(previous: Cue, item: Danmaku, width: Float): Boolean {
        if ((previous.item.mode == 6) != (item.mode == 6)) return false
        val travelled = (item.time - previous.item.time) / previous.lifetime
        val previousSpeed = (video.width() + previous.width) / previous.lifetime
        val nextSpeed = (video.width() + width) / previous.lifetime
        val gap = (video.width() + previous.width) * travelled - previous.width
        val catchUp = (nextSpeed - previousSpeed).coerceAtLeast(0.0) * previous.lifetime * (1 - travelled)
        return gap - catchUp >= paint.textSize * 0.4f
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (!style.enabled || !time.isFinite() || cues.isEmpty()) return
        val earliest = time - maxOf(4.0, 8.0 / style.speed)
        var index = cues.binarySearchBy(earliest) { it.item.time }.let { if (it < 0) -it - 1 else it }
        val alpha = (style.opacity * 255).toInt()
        canvas.save()
        canvas.clipRect(video)
        while (index < cues.size) {
            val cue = cues[index++]
            val elapsed = time - cue.item.time
            if (elapsed < 0) break
            if (elapsed >= cue.lifetime) continue
            val progress = (elapsed / cue.lifetime).toFloat()
            val travel = (video.width() + cue.width) * progress
            val x = when (cue.item.mode) {
                4, 5 -> video.centerX() - cue.width / 2
                6 -> video.left - cue.width + travel
                else -> video.right - travel
            }
            paint.style = Paint.Style.STROKE
            paint.color = Color.BLACK
            paint.alpha = alpha
            canvas.drawText(cue.text, x, cue.y, paint)
            paint.style = Paint.Style.FILL
            paint.color = cue.item.color or 0xff000000.toInt()
            paint.alpha = alpha
            canvas.drawText(cue.text, x, cue.y, paint)
        }
        canvas.restore()
    }
}
