package app.danmaku.player

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Build
import android.util.Xml
import org.xmlpull.v1.XmlPullParser
import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.Locale
import java.util.UUID

object MediaMetadata {
    private val subtitleExtensions = setOf("srt", "ass", "ssa", "vtt")

    fun inspect(context: Context, uri: Uri, name: String, neighbors: Map<String, Uri> = emptyMap()): ScannedMedia {
        val stem = name.substringBeforeLast('.').lowercase(Locale.ROOT)
        val nfo = listOf("$stem.nfo", "movie.nfo", "tvshow.nfo").firstNotNullOfOrNull(neighbors::get)
        val sidecar = nfo?.let { readNfo(context, it) } ?: MediaDetails()
        val poster = listOf("$stem.jpg", "$stem.png", "poster.jpg", "poster.png", "folder.jpg")
            .firstNotNullOfOrNull(neighbors::get)
        val subtitles = neighbors.filterKeys { file ->
            file.startsWith("$stem.") && file.substringAfterLast('.') in subtitleExtensions
        }.values.toList()
        var duration = 0.0
        var title: String? = null
        var artwork = poster
        val retriever = MediaMetadataRetriever()
        try {
            retriever.setDataSource(context, uri)
            duration = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)
                ?.toLongOrNull()?.coerceAtLeast(0)?.div(1000.0) ?: 0.0
            title = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)?.takeIf(String::isNotBlank)
            if (poster == null && duration > 0) artwork = thumbnail(context, retriever, uri, duration)
        } catch (_: Exception) {
            // Unsupported containers can still be opened with libmpv.
        } finally {
            runCatching { retriever.release() }
        }
        return ScannedMedia(uri, name, sidecar.copy(title = sidecar.title ?: title), duration, artwork, subtitles)
    }

    private fun readNfo(context: Context, uri: Uri): MediaDetails? = runCatching {
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readLimited(2_000_000) } ?: return null
        val parser = Xml.newPullParser().apply {
            setFeature(XmlPullParser.FEATURE_PROCESS_DOCDECL, false)
            setInput(ByteArrayInputStream(bytes), null)
        }
        val values = mutableMapOf<String, String>()
        val wanted = setOf("title", "year", "season", "episode", "plot")
        while (parser.next() != XmlPullParser.END_DOCUMENT) {
            if (parser.eventType == XmlPullParser.START_TAG && parser.depth == 2 && parser.name in wanted) {
                val key = parser.name
                values[key] = parser.nextText().trim()
            }
        }
        MediaDetails(values["title"]?.takeIf(String::isNotBlank), values["year"]?.toIntOrNull(),
            values["season"]?.toIntOrNull()?.takeIf { it >= 0 }, values["episode"]?.toIntOrNull()?.takeIf { it >= 0 },
            values["plot"].orEmpty())
    }.getOrNull()

    private fun thumbnail(context: Context, retriever: MediaMetadataRetriever, uri: Uri, duration: Double): Uri? {
        val key = MessageDigest.getInstance("SHA-256").digest(MediaFiles.documentKey(uri).toByteArray())
            .take(16).joinToString("") { "%02x".format(it) }
        val file = File(File(context.cacheDir, "thumbnails"), "$key.jpg")
        if (file.isFile) return Uri.fromFile(file)
        val position = (minOf(duration * 0.1, 30.0) * 1_000_000).toLong()
        if (Build.VERSION.SDK_INT < 27) return null
        val frame = retriever.getScaledFrameAtTime(position, MediaMetadataRetriever.OPTION_CLOSEST_SYNC, 448, 448)
            ?: return null
        return try {
            writeImage(file, frame)
        } finally {
            frame.recycle()
        }
    }

    fun copyArtwork(context: Context, uri: Uri): Uri {
        val bitmap = loadArtwork(context, uri, 720) ?: throw IOException("图片格式无法读取")
        val file = File(File(context.filesDir, "artwork"), "${UUID.randomUUID()}.jpg")
        return try {
            writeImage(file, bitmap)
        } finally {
            bitmap.recycle()
        }
    }

    fun deleteCopiedArtwork(context: Context, uri: Uri?) {
        if (uri?.scheme != "file") return
        val file = uri.path?.let(::File) ?: return
        if (file.parentFile == File(context.filesDir, "artwork")) file.delete()
    }

    fun loadArtwork(context: Context, uri: Uri, target: Int): Bitmap? = runCatching {
        val resolver = context.contentResolver
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        var sample = 1
        while (maxOf(bounds.outWidth, bounds.outHeight) / sample > target * 2) sample *= 2
        resolver.openInputStream(uri)?.use {
            BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample })
        }
    }.getOrNull()

    private fun writeImage(file: File, bitmap: Bitmap): Uri {
        file.parentFile?.mkdirs()
        val atomic = android.util.AtomicFile(file)
        val output = atomic.startWrite()
        try {
            if (!bitmap.compress(Bitmap.CompressFormat.JPEG, 84, output)) throw IOException("无法保存海报")
            atomic.finishWrite(output)
        } catch (error: Exception) {
            atomic.failWrite(output)
            throw error
        }
        return Uri.fromFile(file)
    }
}
