package app.danmaku.player

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.GestureDetector
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceView
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.ToggleButton
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import app.danmaku.player.player.DanmakuOverlay
import app.danmaku.player.player.MpvController
import app.danmaku.player.player.PlayerState
import java.util.Locale
import kotlin.math.roundToInt

class MainActivity : Activity() {
    private var controller: MpvController? = null
    private var state = PlayerState()
    private var entry: MediaEntry? = null
    private var persisted = false
    private var lastSavedAt = 0L
    private var dragging = false
    private var fullscreen = false
    private var controlsVisible = true
    private var foreground = false
    private var keepScreen = false
    private var showSourceAfterLoad = false
    private var playlistIds = emptyList<String>()
    private lateinit var library: MediaLibrary
    private lateinit var danmaku: DanmakuControls
    private lateinit var danmakuOverlay: DanmakuOverlay
    private lateinit var player: FrameLayout
    private lateinit var panelHost: FrameLayout
    private lateinit var toolbar: LinearLayout
    private lateinit var controls: LinearLayout
    private lateinit var title: TextView
    private lateinit var status: TextView
    private lateinit var elapsed: TextView
    private lateinit var duration: TextView
    private lateinit var fullScreenButton: Button
    private lateinit var danmakuToggle: ToggleButton
    private lateinit var danmakuStatus: TextView
    private lateinit var seek: SeekBar
    private lateinit var play: Button
    private lateinit var audio: Button
    private lateinit var subtitles: Button
    private lateinit var comments: Button
    private lateinit var options: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        library = MediaLibrary(this)
        playlistIds = intent.getStringArrayListExtra(PLAYLIST).orEmpty()
        showSourceAfterLoad = savedInstanceState == null && intent.getBooleanExtra(SOURCE_MENU, false)
        val surface = SurfaceView(this)
        setContentView(createLayout(surface))
        danmaku = DanmakuControls(this, panelHost, danmakuOverlay, ::currentEntry, ::updateEntry, { state.position },
            { pickFile(PICK_XML) }, ::setDanmakuStatus, ::updateDanmakuToggle, ::panelVisibilityChanged)
        updateDanmakuToggle(danmakuOverlay.style.enabled)
        render(state)
        if (Build.VERSION.SDK_INT >= 33) onBackInvokedDispatcher.registerOnBackInvokedCallback(0, ::goBack)

        controller = try {
            MpvController(this, ::render)
        } catch (error: LinkageError) {
            Log.e("DanmakuMpv", "Could not load libmpv", error)
            render(state.copy(error = "此设备无法载入 libmpv"))
            return
        } catch (error: IllegalStateException) {
            Log.e("DanmakuMpv", "Could not initialize libmpv", error)
            render(state.copy(error = "libmpv 初始化失败"))
            return
        }
        controller?.let(surface.holder::addCallback)
        val entryId = savedInstanceState?.getString(ENTRY_ID) ?: intent.getStringExtra(ENTRY_ID)
        val stored = entryId?.let(library::get)
        val incoming = savedInstanceState?.getString("uri")?.let(Uri::parse)
            ?: intent.takeIf { it.action == Intent.ACTION_VIEW }?.data
        when {
            stored != null -> open(stored, inLibrary = true)
            incoming != null && incoming.scheme == "content" -> openPicked(incoming, intent.flags)
        }
        if (savedInstanceState?.getBoolean("fullscreen") == true) setFullscreen(true)
        controlsVisible = savedInstanceState?.getBoolean("controlsVisible", controlsVisible) ?: controlsVisible
        updateChrome()
    }

    private fun createLayout(surface: SurfaceView) = FrameLayout(this).apply {
        setBackgroundColor(Color.BLACK)
        applySystemInsets(this, includeIme = true)
        player = FrameLayout(context).apply {
            addView(surface, FrameLayout.LayoutParams(-1, -1))
            danmakuOverlay = DanmakuOverlay(context).apply {
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }
            addView(danmakuOverlay, FrameLayout.LayoutParams(-1, -1))
            val touch = View(context).apply {
                contentDescription = "视频画面，显示或隐藏播放控制"
                isFocusable = true
                setOnClickListener {
                    controlsVisible = !controlsVisible
                    updateChrome()
                }
                val gestures = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
                    override fun onDown(event: MotionEvent) = true
                    override fun onSingleTapConfirmed(event: MotionEvent) = performClick()
                    override fun onDoubleTap(event: MotionEvent): Boolean {
                        val relative = event.x / width.coerceAtLeast(1)
                        when {
                            relative < 0.35f -> jump(-5.0)
                            relative > 0.65f -> jump(5.0)
                            else -> controller?.togglePlayback()
                        }
                        return true
                    }
                })
                setOnTouchListener { _, event -> gestures.onTouchEvent(event) }
            }
            addView(touch, FrameLayout.LayoutParams(-1, -1))
            title = heading("", 17f).apply { maxLines = 3 }
            toolbar = LinearLayout(context).apply {
                gravity = Gravity.CENTER_VERTICAL
                background = chromeShade(top = true)
                setPadding(dp(4), dp(4), dp(12), dp(12))
                addView(uiButton("‹", "返回媒体库") { goBack() })
                addView(title, LinearLayout.LayoutParams(0, -2, 1f))
            }
            addView(toolbar, FrameLayout.LayoutParams(-1, -2, Gravity.TOP))
            play = uiButton("▶", "播放") { controller?.togglePlayback() }.apply {
                background = ripple(0xc0202020.toInt(), 36)
                setPadding(0, 0, 0, 0)
                textSize = 23f
            }
            addView(play, FrameLayout.LayoutParams(dp(64), dp(64), Gravity.CENTER))
            controls = createPlaybackControls()
            addView(controls, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM))
        }
        addView(player, FrameLayout.LayoutParams(-1, -1))
        panelHost = FrameLayout(context).apply { visibility = View.GONE }
        addView(panelHost, FrameLayout.LayoutParams(-1, -1))
    }

    private fun createPlaybackControls() = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(8), dp(12), dp(8), 0)
        background = chromeShade(top = false)
        status = TextView(context).apply { setPadding(dp(6), 0, dp(6), 0) }
        addView(status)
        danmakuStatus = TextView(context).apply {
            setPadding(dp(6), 0, dp(6), 0)
            visibility = View.GONE
            maxLines = 2
            accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        addView(danmakuStatus)
        elapsed = TextView(context).apply {
            setPadding(dp(6), 0, 0, 0)
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }
        duration = TextView(context).apply {
            setPadding(0, 0, dp(6), 0)
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }
        seek = SeekBar(context).apply {
            max = 1000
            minHeight = dp(48)
            contentDescription = "播放进度"
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(bar: SeekBar, progress: Int, fromUser: Boolean) {
                    if (!fromUser) return
                    updatePlaybackTime(state.duration * progress / bar.max, state.duration)
                    if (!dragging) seekTo(progress)
                }
                override fun onStartTrackingTouch(bar: SeekBar) { dragging = true }
                override fun onStopTrackingTouch(bar: SeekBar) {
                    dragging = false
                    seekTo(bar.progress)
                }
            })
        }
        addView(LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            addView(elapsed)
            addView(seek, LinearLayout.LayoutParams(0, -2, 1f))
            addView(duration)
        })
        danmakuToggle = ToggleButton(context).apply {
            textOn = "弹幕开"
            textOff = "弹幕关"
            isAllCaps = false
            minHeight = dp(48)
            minWidth = dp(48)
            background = ripple()
            setPadding(dp(8), dp(4), dp(8), dp(4))
            contentDescription = "弹幕开关"
            setOnClickListener { danmaku.toggle() }
        }
        comments = uiButton("弹幕设置") { danmaku.show() }
        subtitles = uiButton("字幕") { subtitleMenu() }
        audio = uiButton("音轨") { showTracks("audio") }
        fullScreenButton = uiButton("⛶", "进入全屏") { setFullscreen(!fullscreen) }
        options = uiButton("⋯", "更多播放选项") { playbackOptions() }
        addView(LinearLayout(context).apply {
            addView(buttonStrip(danmakuToggle, comments, subtitles, audio), LinearLayout.LayoutParams(0, -2, 1f))
            addView(fullScreenButton)
            addView(options)
        })
    }

    private fun chromeShade(top: Boolean) = GradientDrawable(
        if (top) GradientDrawable.Orientation.TOP_BOTTOM else GradientDrawable.Orientation.BOTTOM_TOP,
        intArrayOf(0xf0000000.toInt(), 0xc4000000.toInt(), Color.TRANSPARENT),
    )

    private fun updatePlaybackTime(position: Double, total: Double) {
        val current = playbackTime(position)
        val end = playbackTime(total)
        if (elapsed.text != current) elapsed.text = current
        if (duration.text != end) duration.text = end
        val description = "$current / $end"
        if (Build.VERSION.SDK_INT >= 30) {
            if (seek.stateDescription != description) seek.stateDescription = description
        } else {
            val label = "播放进度，$description"
            if (seek.contentDescription != label) seek.contentDescription = label
        }
    }

    private fun updateDanmakuToggle(enabled: Boolean) {
        danmakuToggle.isChecked = enabled
    }

    private fun panelVisibilityChanged(visible: Boolean) {
        player.importantForAccessibility =
            if (visible) View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS else View.IMPORTANT_FOR_ACCESSIBILITY_AUTO
        updateChrome()
    }

    @Suppress("DEPRECATION")
    private fun pickFile(request: Int) {
        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE)
            type = "*/*"
        }
        try {
            startActivityForResult(intent, request)
        } catch (_: android.content.ActivityNotFoundException) {
            toast("系统文件选择器不可用")
        }
    }

    @Deprecated("The player uses platform document pickers.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode != RESULT_OK || data == null) return
        val uri = data.data ?: return
        when (requestCode) {
            PICK_VIDEO -> openPicked(uri, data.flags)
            PICK_SUBTITLE -> controller?.addSubtitle(uri)
            PICK_XML -> danmaku.loadXml(uri)
        }
    }

    private fun openPicked(uri: Uri, flags: Int) {
        val name = runCatching { MediaFiles.displayName(contentResolver, uri) }.getOrDefault("视频")
        val mime = runCatching { contentResolver.getType(uri) }.getOrNull()
        if (!MediaFiles.isVideo(name, mime)) {
            toast("请选择视频文件")
            return
        }
        val inLibrary = MediaFiles.keepAccess(contentResolver, uri, flags)
        val next = if (inLibrary) {
            library.rememberRoot(uri, false)
            library.add(listOf(ScannedMedia(uri, name))).single()
        } else MediaEntry(uri = uri, name = name)
        open(next, inLibrary)
    }

    private fun open(next: MediaEntry, inLibrary: Boolean) {
        if (state.loading) return
        saveProgress()
        if (!persisted) LocalDanmaku.delete(this, entry?.xml)
        entry = next
        persisted = inLibrary
        lastSavedAt = SystemClock.elapsedRealtime()
        title.text = listOfNotNull(next.title, next.episode?.uppercase(Locale.ROOT)).joinToString("  ")
        danmaku.reload(next)
        controller?.load(next.uri, next.resumePosition, next.sidecarSubtitles)
    }

    private fun updateEntry(updated: MediaEntry) {
        entry = updated
        if (persisted) library.save(updated)
        danmakuOverlay.update(updated.sourceTime(state.position), state.aspect)
    }

    private fun currentEntry(): MediaEntry? {
        val item = entry ?: return null
        if (!persisted) return item
        return library.get(item.id)?.takeIf { it.uri == item.uri }
    }

    private fun saveProgress() {
        if (!persisted || !state.loaded || !state.duration.isFinite() || state.duration <= 0 || !state.position.isFinite()) return
        val current = currentEntry() ?: return
        val position = state.position.coerceIn(0.0, state.duration)
        val watched = current.watched || position / state.duration > 0.95
        if (current.position == position && current.duration == state.duration && current.watched == watched) return
        lastSavedAt = SystemClock.elapsedRealtime()
        updateEntry(current.copy(position = position, duration = state.duration, watched = watched,
            playedAt = System.currentTimeMillis()))
    }

    private fun playbackOptions() {
        val labels = mutableListOf("打开其他视频")
        val actions = mutableListOf<() -> Unit>({ pickFile(PICK_VIDEO) })
        if (playlistIds.isNotEmpty()) {
            labels.add("播放列表")
            actions.add(::showPlaylist)
        }
        if (state.error != null) {
            labels.add("关闭错误提示")
            actions.add { controller?.dismissError() }
        }
        if (state.loaded) {
            labels += listOf("播放速度 · ${state.speed}×", "后退 5 秒", "前进 5 秒")
            actions += listOf(::showSpeeds, { jump(-5.0) }, { jump(5.0) })
            labels.add("音量 · ${state.volume.roundToInt()}%")
            actions.add(::volumeDialog)
        }
        AlertDialog.Builder(this).setTitle("播放选项").setItems(labels.toTypedArray()) { _, index ->
            actions[index]()
        }.show()
    }

    private fun showPlaylist() {
        val items = playlistIds.mapNotNull(library::get)
        if (items.isEmpty()) {
            toast("播放列表为空")
            return
        }
        val selected = items.indexOfFirst { it.id == entry?.id }
        val labels = items.map { listOfNotNull(it.title, it.episode).joinToString("  ") }
        AlertDialog.Builder(this).setTitle("播放列表").setSingleChoiceItems(labels.toTypedArray(), selected) { dialog, index ->
            dialog.dismiss()
            val next = items[index]
            if (next.id == entry?.id) return@setSingleChoiceItems
            if (MediaFiles.readable(contentResolver, next.uri)) open(next, inLibrary = true)
            else {
                library.save(next.copy(available = false))
                toast("文件不可用，请在媒体库重新定位")
            }
        }.setNegativeButton("关闭", null).show()
    }

    private fun volumeDialog() {
        val level = TextView(this).apply { text = "${state.volume.roundToInt()}%" }
        val slider = SeekBar(this).apply {
            max = 100
            progress = state.volume.roundToInt().coerceIn(0, 100)
            contentDescription = "播放音量"
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(bar: SeekBar, progress: Int, fromUser: Boolean) {
                    if (fromUser) controller?.setVolume(progress)
                    level.text = "$progress%"
                }
                override fun onStartTrackingTouch(bar: SeekBar) = Unit
                override fun onStopTrackingTouch(bar: SeekBar) = Unit
            })
        }
        val form = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), 0, dp(20), 0)
            addView(level)
            addView(slider)
        }
        AlertDialog.Builder(this).setTitle("播放音量").setView(form).setPositiveButton("完成", null).show()
    }

    private fun showSpeeds() {
        val rates = listOf(0.5, 0.75, 1.0, 1.25, 1.5, 2.0)
        AlertDialog.Builder(this).setTitle("播放速度")
            .setSingleChoiceItems(rates.map { "$it×" }.toTypedArray(), rates.indexOf(state.speed)) { dialog, index ->
                controller?.setSpeed(rates[index])
                dialog.dismiss()
            }.show()
    }

    private fun subtitleMenu() {
        AlertDialog.Builder(this).setTitle("字幕")
            .setItems(arrayOf("选择字幕轨", "加载外部字幕", "字幕延迟 · ${String.format(Locale.ROOT, "%+.1f 秒", state.subtitleDelay)}")) { _, index ->
                when (index) {
                    0 -> showTracks("sub")
                    1 -> pickFile(PICK_SUBTITLE)
                    2 -> showSubtitleDelay()
                }
            }.show()
    }

    private fun showSubtitleDelay() {
        val current = state.subtitleDelay
        val delays = listOf(current - 1, current - 0.1, 0.0, current + 0.1, current + 1)
        AlertDialog.Builder(this).setTitle("字幕延迟")
            .setItems(arrayOf("提前 1 秒", "提前 0.1 秒", "重设为 0", "延后 0.1 秒", "延后 1 秒")) { _, index ->
                controller?.setSubtitleDelay(delays[index])
            }.show()
    }

    private fun showTracks(type: String) {
        val tracks = state.tracks.filter { it.type == type }
        if (tracks.isEmpty()) {
            toast(if (type == "audio") "没有可用音轨" else "这个视频没有字幕轨")
            return
        }
        val labels = tracks.map { it.label }.toMutableList()
        if (type == "sub") labels.add("关闭字幕")
        val selected = tracks.indexOfFirst { it.selected }.let { if (it < 0 && type == "sub") tracks.size else it }
        AlertDialog.Builder(this).setTitle(if (type == "audio") "音轨" else "字幕轨")
            .setSingleChoiceItems(labels.toTypedArray(), selected) { dialog, index ->
                controller?.selectTrack(type, tracks.getOrNull(index))
                dialog.dismiss()
            }.setNegativeButton("取消", null).show()
    }

    private fun jump(seconds: Double) {
        controller?.seekTo(state.position + seconds)
    }

    private fun seekTo(progress: Int) = controller?.seekTo(state.duration * progress / seek.max)

    private fun render(next: PlayerState) {
        if (next.error != null && next.error != state.error) controlsVisible = true
        state = next
        val statusText = when {
            next.error != null -> next.error
            next.loading -> "正在打开视频…"
            next.loaded -> {
                val decoder = when (next.decoder) {
                    null -> "等待解码器"
                    "no" -> "软件解码"
                    else -> next.decoder
                }
                decoder
            }
            else -> "从媒体库选择视频"
        }
        if (status.text.toString() != statusText) status.text = statusText
        options.isEnabled = next.ready && !next.loading
        play.isEnabled = next.loaded
        val playText = if (!next.loaded || next.paused || next.ended) "播放" else "暂停"
        val glyph = if (playText == "播放") "▶" else "Ⅱ"
        if (play.text != glyph) play.text = glyph
        if (play.contentDescription != playText) play.contentDescription = playText
        audio.isEnabled = next.loaded && next.tracks.any { it.type == "audio" }
        subtitles.isEnabled = next.loaded
        comments.isEnabled = next.loaded
        danmakuToggle.isEnabled = next.loaded
        fullScreenButton.isEnabled = next.loaded
        seek.isEnabled = next.loaded && next.duration > 0
        if (!dragging) {
            updatePlaybackTime(next.position, next.duration)
            seek.progress = if (next.duration > 0) {
                ((next.position / next.duration).coerceIn(0.0, 1.0) * seek.max).roundToInt()
            } else 0
        }
        danmakuOverlay.update(if (next.loaded) entry?.sourceTime(next.position) ?: next.position else Double.NaN, next.aspect)
        if (next.loaded && SystemClock.elapsedRealtime() - lastSavedAt >= 5000) saveProgress()
        updateScreenPolicy()
        updateChrome()
        if (showSourceAfterLoad && next.loaded) {
            showSourceAfterLoad = false
            controller?.pause()
            danmaku.show()
        }
    }

    private fun updateScreenPolicy() {
        val active = foreground && state.loaded && !state.paused && !state.ended
        if (active == keepScreen) return
        keepScreen = active
        if (active) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    private fun setDanmakuStatus(message: String) {
        danmakuStatus.text = message
        danmakuStatus.visibility = if (message.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun setFullscreen(enabled: Boolean) {
        fullscreen = enabled
        controlsVisible = !enabled
        if (Build.VERSION.SDK_INT >= 30) {
            window.insetsController?.let {
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                if (enabled) it.hide(WindowInsets.Type.systemBars()) else it.show(WindowInsets.Type.systemBars())
            }
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = if (enabled) (
                View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            ) else 0
        }
        updateChrome()
    }

    private fun updateChrome() {
        val visible = controlsVisible && panelHost.visibility != View.VISIBLE
        toolbar.visibility = if (visible) View.VISIBLE else View.GONE
        controls.visibility = if (visible) View.VISIBLE else View.GONE
        play.visibility = if (visible && state.loaded) View.VISIBLE else View.GONE
        fullScreenButton.contentDescription = if (fullscreen) "退出全屏" else "进入全屏"
    }

    private fun goBack() {
        if (::danmaku.isInitialized && danmaku.back()) return
        if (fullscreen) setFullscreen(false) else finish()
    }

    @Deprecated("Used before API 33.")
    override fun onBackPressed() = goBack()

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean {
        if (!state.loaded || panelHost.visibility == View.VISIBLE) return super.onKeyUp(keyCode, event)
        when (keyCode) {
            KeyEvent.KEYCODE_SPACE, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> controller?.togglePlayback()
            KeyEvent.KEYCODE_DPAD_LEFT -> if (seek.hasFocus()) return super.onKeyUp(keyCode, event) else jump(-5.0)
            KeyEvent.KEYCODE_DPAD_RIGHT -> if (seek.hasFocus()) return super.onKeyUp(keyCode, event) else jump(5.0)
            KeyEvent.KEYCODE_F -> setFullscreen(!fullscreen)
            KeyEvent.KEYCODE_D -> danmaku.toggle()
            else -> return super.onKeyUp(keyCode, event)
        }
        return true
    }

    override fun onSaveInstanceState(outState: Bundle) {
        saveProgress()
        outState.putString(ENTRY_ID, entry?.id?.takeIf { persisted })
        outState.putString("uri", entry?.uri?.toString())
        outState.putBoolean("fullscreen", fullscreen)
        outState.putBoolean("controlsVisible", controlsVisible)
        super.onSaveInstanceState(outState)
    }

    override fun onPause() {
        foreground = false
        updateScreenPolicy()
        saveProgress()
        super.onPause()
    }

    override fun onResume() {
        super.onResume()
        foreground = true
        updateScreenPolicy()
    }

    override fun onStop() {
        controller?.pause()
        super.onStop()
    }

    override fun onDestroy() {
        if (::danmaku.isInitialized) danmaku.close()
        if (!persisted) LocalDanmaku.delete(this, entry?.xml)
        controller?.close()
        super.onDestroy()
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()

    companion object {
        const val ENTRY_ID = "app.danmaku.player.ENTRY_ID"
        const val PLAYLIST = "app.danmaku.player.PLAYLIST"
        const val SOURCE_MENU = "app.danmaku.player.SOURCE_MENU"
        private const val PICK_VIDEO = 1
        private const val PICK_SUBTITLE = 2
        private const val PICK_XML = 3
    }
}
