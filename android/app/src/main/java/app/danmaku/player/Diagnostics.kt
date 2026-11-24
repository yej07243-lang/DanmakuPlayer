package app.danmaku.player

import android.content.Context
import android.util.Log
import java.io.File

object Diagnostics {
    @Synchronized
    fun record(context: Context, tag: String, message: String) {
        Log.i(tag, message)
        runCatching {
            val file = File(context.filesDir, "diagnostics.log")
            if (file.length() > 512_000) file.delete()
            file.appendText("${System.currentTimeMillis()} [$tag] ${message.take(300)}\n")
        }
    }

    @Synchronized
    fun snapshot(context: Context): ByteArray =
        File(context.filesDir, "diagnostics.log").takeIf(File::isFile)?.readBytes()
            ?: "暂无诊断日志\n".toByteArray()
}
