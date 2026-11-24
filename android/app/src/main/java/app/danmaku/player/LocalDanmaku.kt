package app.danmaku.player

import android.content.Context
import android.net.Uri
import java.io.File
import java.io.IOException
import java.util.UUID

object LocalDanmaku {
    private const val LIMIT = 64_000_000

    data class Imported(val uri: Uri, val comments: List<Danmaku>)

    fun copy(context: Context, uri: Uri): Imported {
        val directory = folder(context).apply { mkdirs() }
        val file = File(directory, "${UUID.randomUUID()}.xml")
        try {
            context.contentResolver.openInputStream(uri)?.use { input ->
                file.outputStream().use { output ->
                    val buffer = ByteArray(8192)
                    var total = 0
                    while (true) {
                        if (Thread.currentThread().isInterrupted) throw InterruptedException()
                        val read = input.read(buffer)
                        if (read < 0) break
                        total += read
                        if (total > LIMIT) throw IOException("弹幕 XML 超过 64 MB")
                        output.write(buffer, 0, read)
                    }
                }
            } ?: throw IOException("无法读取弹幕 XML")
            val comments = file.inputStream().use(DanmakuParser::xml)
            return Imported(Uri.fromFile(file), comments)
        } catch (error: Exception) {
            file.delete()
            throw error
        }
    }

    fun load(context: Context, uri: Uri): List<Danmaku> {
        val file = privateFile(context, uri)
        if (file != null) return file.inputStream().use(DanmakuParser::xml)
        if (uri.scheme != "content") throw IOException("本地弹幕文件不可用")
        // Previous builds stored the document URI. Keep those associations readable.
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readLimited(LIMIT) }
            ?: throw IOException("本地弹幕文件不可用")
        return bytes.inputStream().use(DanmakuParser::xml)
    }

    fun delete(context: Context, uri: Uri?) {
        privateFile(context, uri)?.delete()
    }

    private fun folder(context: Context) = File(context.filesDir, "local-danmaku")

    private fun privateFile(context: Context, uri: Uri?): File? {
        if (uri?.scheme != "file") return null
        return uri.path?.let(::File)?.takeIf { it.parentFile == folder(context) }
    }
}
