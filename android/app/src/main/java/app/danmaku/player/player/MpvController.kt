package app.danmaku.player.player

import android.content.Context
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.OpenableColumns
import android.util.Log
import android.view.SurfaceHolder
import app.danmaku.player.Diagnostics
import dev.jdtech.mpv.MPVLib
import dev.jdtech.mpv.MPVLib.MpvEvent
import dev.jdtech.mpv.MPVLib.MpvFormat
import java.io.IOException

data class PlayerTrack(
    val id: Int,
    val type: String,
    val label: String,
    val selected: Boolean,
)

data class PlayerState(
    val ready: Boolean = false,
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val paused: Boolean = true,
    val position: Double = 0.0,
    val duration: Double = 0.0,
    val decoder: String? = null,
    val aspect: Double = 0.0,
    val speed: Double = 1.0,
    val subtitleDelay: Double = 0.0,
    val volume: Double = 100.0,
    val ended: Boolean = false,
    val tracks: List<PlayerTrack> = emptyList(),
    val error: String? = null,
)

class MpvController(
    context: Context,
    private val onStateChanged: (PlayerState) -> Unit,
) : SurfaceHolder.Callback, MPVLib.EventObserver, MPVLib.LogObserver {
    private data class LoadRequest(val uri: Uri, val position: Double, val subtitles: List<Uri>)

    private val app = context.applicationContext
    private val resolver = context.contentResolver
    private val main = Handler(Looper.getMainLooper())
    private val mpv = MPVLib.create(context.applicationContext) ?: error("无法创建 libmpv")
    private var state = PlayerState()
    private var closed = false
    private var attached = false
    private var pendingVideo: LoadRequest? = null
    private var pendingSubtitles = emptyList<Uri>()

    init {
        mpv.setOptionString("vo", "gpu")
        mpv.setOptionString("gpu-context", "android")
        mpv.setOptionString("gpu-sw", "yes")
        mpv.setOptionString("hwdec", "mediacodec-copy")
        mpv.setOptionString("idle", "yes")
        mpv.setOptionString("keep-open", "yes")
        mpv.setOptionString("sub-auto", "no")
        mpv.init()
        mpv.addObserver(this)
        mpv.addLogObserver(this)
        mpv.requestLogMessages("info")
        listOf("time-pos", "duration", "pause", "track-list", "aid", "sid", "hwdec-current", "speed", "sub-delay",
            "volume", "eof-reached").forEach {
            mpv.observeProperty(it, MpvFormat.MPV_FORMAT_NONE)
        }
        Diagnostics.record(app, TAG, "Initialized ${mpv.getPropertyString("mpv-version")}")
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        if (closed) return
        mpv.attachSurface(holder.surface)
        attached = true
        mpv.setPropertyString("vo", "gpu")
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        if (closed || !attached || width <= 0 || height <= 0) return
        mpv.setPropertyString("android-surface-size", "${width}x${height}")
        publish(state.copy(ready = true))
        pendingVideo?.let {
            pendingVideo = null
            load(it.uri, it.position, it.subtitles)
        }
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        if (closed || !attached) return
        mpv.setPropertyString("vo", "null")
        mpv.detachSurface()
        attached = false
        publish(state.copy(ready = false))
    }

    fun load(uri: Uri, position: Double = 0.0, subtitles: List<Uri> = emptyList()) {
        if (closed || state.loading) return
        if (!state.ready) {
            pendingVideo = LoadRequest(uri, position, subtitles)
            return
        }
        publish(PlayerState(ready = true, speed = state.speed, subtitleDelay = state.subtitleDelay, volume = state.volume))
        val source = openSource(uri)
        if (source == null) {
            mpv.command(arrayOf("stop"))
            return
        }
        pendingSubtitles = subtitles
        publish(state.copy(loading = true))
        if (position > 0) mpv.command(arrayOf("loadfile", source, "replace", "-1", "start=$position"))
        else mpv.command(arrayOf("loadfile", source, "replace"))
        mpv.setPropertyBoolean("pause", false)
    }

    fun addSubtitle(uri: Uri) {
        if (closed || !state.loaded) return
        val source = openSource(uri) ?: return
        val name = subtitleName(uri)
        val previousTracks = state.tracks.count { it.type == "sub" }
        publish(state.copy(error = null))
        mpv.command(arrayOf("sub-add", source, "select", name))
        refreshTracks()
        if (state.tracks.count { it.type == "sub" } <= previousTracks) {
            publish(state.copy(error = "无法加载外部字幕，请选择 SRT、ASS、VTT 或 SSA 文件"))
        }
    }

    private fun subtitleName(uri: Uri) = runCatching {
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
                if (it.moveToFirst()) it.getString(0) else null
            }
    }.getOrNull() ?: "外部字幕"

    private fun openSource(uri: Uri, report: Boolean = true): String? {
        if (uri.scheme != "content") {
            if (report) publish(state.copy(error = "请选择系统文件选择器中的本地文件"))
            return null
        }
        return try {
            val descriptor = resolver.openFileDescriptor(uri, "r") ?: throw IOException("文件无法打开")
            descriptor.use { "fdclose://${it.detachFd()}" }
        } catch (_: IOException) {
            if (report) publish(state.copy(error = "无法读取文件，请检查文件是否仍可用"))
            null
        } catch (_: SecurityException) {
            if (report) publish(state.copy(error = "文件读取权限已失效，请重新选择文件"))
            null
        }
    }

    fun togglePlayback() {
        if (state.loaded && !closed) {
            if (state.ended) seekTo(0.0)
            mpv.setPropertyBoolean("pause", if (state.ended) false else !state.paused)
        }
    }

    fun pause() {
        if (!closed && (state.loading || state.loaded)) mpv.setPropertyBoolean("pause", true)
    }

    fun seekTo(seconds: Double) {
        if (!closed && state.loaded && state.duration > 0.0 && seconds.isFinite()) {
            mpv.command(arrayOf("seek", seconds.coerceIn(0.0, state.duration).toString(), "absolute"))
        }
    }

    fun setSpeed(speed: Double) {
        if (!closed && state.loaded) mpv.setPropertyDouble("speed", speed.coerceIn(0.25, 4.0))
    }

    fun setSubtitleDelay(delay: Double) {
        if (!closed && state.loaded) mpv.setPropertyDouble("sub-delay", delay.coerceIn(-60.0, 60.0))
    }

    fun setVolume(volume: Int) {
        if (!closed && state.loaded) mpv.setPropertyDouble("volume", volume.coerceIn(0, 100).toDouble())
    }

    fun dismissError() = publish(state.copy(error = null))

    fun selectTrack(type: String, track: PlayerTrack?) {
        if (!closed && state.loaded) {
            val property = if (type == "audio") "aid" else "sid"
            mpv.setPropertyString(property, track?.id?.toString() ?: "no")
            refreshTracks()
        }
    }

    override fun event(eventId: Int) {
        main.post {
            if (closed) return@post
            when (eventId) {
                MpvEvent.MPV_EVENT_FILE_LOADED -> {
                    pendingSubtitles.forEach { uri ->
                        openSource(uri, report = false)?.let { source ->
                            mpv.command(arrayOf("sub-add", source, "auto", subtitleName(uri)))
                        }
                    }
                    pendingSubtitles = emptyList()
                    publish(state.copy(loading = false, loaded = true,
                        duration = mpv.getPropertyDouble("duration") ?: state.duration,
                        position = mpv.getPropertyDouble("time-pos") ?: state.position))
                    refreshTracks()
                    Diagnostics.record(app, TAG, "File loaded with ${state.tracks.count { it.type == "audio" }} audio tracks")
                }
                MpvEvent.MPV_EVENT_PLAYBACK_RESTART -> {
                    refreshAspect()
                    refreshDecoder()
                    Log.i(TAG, "Playback restart at ${state.position}s with decoder ${state.decoder}")
                }
            }
        }
    }

    override fun eventProperty(property: String) {
        main.post {
            if (closed) return@post
            when (property) {
                "time-pos" -> publish(state.copy(position = mpv.getPropertyDouble(property) ?: 0.0))
                "duration" -> publish(state.copy(duration = mpv.getPropertyDouble(property) ?: 0.0))
                "pause" -> publish(state.copy(paused = mpv.getPropertyBoolean(property) ?: true))
                "track-list", "aid", "sid" -> refreshTracks()
                "hwdec-current" -> refreshDecoder()
                "speed" -> publish(state.copy(speed = mpv.getPropertyDouble(property) ?: 1.0))
                "sub-delay" -> publish(state.copy(subtitleDelay = mpv.getPropertyDouble(property) ?: 0.0))
                "volume" -> publish(state.copy(volume = mpv.getPropertyDouble(property) ?: 100.0))
                "eof-reached" -> publish(state.copy(ended = mpv.getPropertyBoolean(property) == true))
            }
        }
    }

    override fun eventEndFile(reason: Int, error: Int) {
        if (error == 0) return
        main.post {
            if (!closed) {
                Diagnostics.record(app, TAG, "Playback failed (mpv $error)")
                publish(PlayerState(ready = state.ready, volume = state.volume, error = "播放失败（mpv $error）"))
            }
        }
    }

    private fun refreshDecoder() {
        val decoder = mpv.getPropertyString("hwdec-current")
        if (decoder != state.decoder) {
            publish(state.copy(decoder = decoder))
            Diagnostics.record(app, TAG, "hwdec-current=${decoder ?: "unavailable"}")
        }
    }

    private fun refreshAspect() {
        val width = mpv.getPropertyInt("video-out-params/dw") ?: return
        val height = mpv.getPropertyInt("video-out-params/dh") ?: return
        if (width > 0 && height > 0) publish(state.copy(aspect = width.toDouble() / height))
    }

    private fun refreshTracks() {
        val count = mpv.getPropertyInt("track-list/count") ?: 0
        val tracks = buildList {
            repeat(count) { index ->
                val prefix = "track-list/$index"
                val type = mpv.getPropertyString("$prefix/type") ?: return@repeat
                if (type != "audio" && type != "sub") return@repeat
                val id = mpv.getPropertyInt("$prefix/id") ?: return@repeat
                val title = mpv.getPropertyString("$prefix/title")
                val language = mpv.getPropertyString("$prefix/lang")
                val codec = mpv.getPropertyString("$prefix/codec")
                val label = listOfNotNull(title, language, codec).distinct().joinToString(" · ")
                val selected = mpv.getPropertyBoolean("$prefix/selected") ?: false
                add(PlayerTrack(id, type, "#$id ${label.ifEmpty { type }}", selected))
            }
        }
        publish(state.copy(tracks = tracks))
    }

    override fun logMessage(prefix: String, level: Int, text: String) {
        val message = text.trimEnd()
        when (level) {
            in 0..20 -> Log.e(TAG, "$prefix: $message")
            30 -> Log.w(TAG, "$prefix: $message")
            else -> Log.i(TAG, "$prefix: $message")
        }
    }

    private fun publish(next: PlayerState) {
        if (state == next) return
        state = next
        onStateChanged(next)
    }

    fun close() {
        if (closed) return
        closed = true
        if (attached) {
            mpv.setPropertyString("vo", "null")
            mpv.detachSurface()
        }
        mpv.destroy()
        mpv.removeObserver(this)
        mpv.removeLogObserver(this)
        main.removeCallbacksAndMessages(null)
    }

    private companion object {
        const val TAG = "DanmakuMpv"
    }
}
