package app.danmaku.player

import android.util.Xml
import org.xmlpull.v1.XmlPullParser
import java.io.InputStream
import java.io.IOException

data class Danmaku(val id: String, val time: Double, val mode: Int, val color: Int, val text: String)

object DanmakuParser {
    fun xml(input: InputStream): List<Danmaku> {
        val parser = Xml.newPullParser()
        parser.setFeature(XmlPullParser.FEATURE_PROCESS_DOCDECL, false)
        parser.setInput(input, null)
        val entries = mutableListOf<Danmaku>()
        var event = parser.eventType
        while (event != XmlPullParser.END_DOCUMENT) {
            if (event == XmlPullParser.START_TAG && parser.name == "d") {
                val fields = parser.getAttributeValue(null, "p")?.split(',').orEmpty()
                val comment = parser.nextText()
                val time = fields.getOrNull(0)?.toDoubleOrNull()
                val mode = fields.getOrNull(1)?.toIntOrNull()
                val color = fields.getOrNull(3)?.toIntOrNull()
                if (time != null && time in 0.0..100_000_000.0 && mode != null && mode in 1..6 &&
                    color != null && comment.length in 1..500) {
                    entries.add(Danmaku(fields.getOrNull(7) ?: "xml-${entries.size}", time, mode, color, comment))
                }
            }
            event = parser.next()
        }
        return entries.sortedBy(Danmaku::time)
    }

    fun protobuf(data: ByteArray): List<Danmaku> = buildList {
        val reader = ProtoReader(data)
        while (!reader.done) {
            val tag = reader.tag()
            if (tag == 10L) readEntry(reader.message())?.let(::add)
            else reader.skip(tag)
        }
    }

    private fun readEntry(reader: ProtoReader): Danmaku? {
        var id = 0L
        var progress = 0L
        var mode = 1L
        var color = 0xffffffL
        var text = ""
        while (!reader.done) {
            when (val tag = reader.tag()) {
                8L -> id = reader.varint()
                16L -> progress = reader.varint()
                24L -> mode = reader.varint()
                40L -> color = reader.varint()
                58L -> text = reader.message().utf8()
                else -> reader.skip(tag)
            }
        }
        if (mode !in 1..6 || progress < 0 || text.length !in 1..500) return null
        val key = if (id != 0L) java.lang.Long.toUnsignedString(id) else "$progress:$mode:$text"
        return Danmaku(key, progress / 1000.0, mode.toInt(), color.toInt(), text)
    }
}

private class ProtoReader(private val data: ByteArray, private var index: Int = 0, private val end: Int = data.size) {
    val done get() = index == end

    fun tag(): Long = varint().also { if (it <= 0) invalid() }

    fun varint(): Long {
        var value = 0L
        for (shift in 0..63 step 7) {
            if (done) invalid()
            val byte = data[index++].toInt() and 0xff
            if (shift == 63 && byte > 1) invalid()
            value = value or ((byte and 0x7f).toLong() shl shift)
            if (byte and 0x80 == 0) return value
        }
        invalid()
    }

    fun message(): ProtoReader {
        val size = varint()
        if (size < 0 || size > end - index) invalid()
        val start = index
        index += size.toInt()
        return ProtoReader(data, start, index)
    }

    fun utf8() = if (end - index > 2_000) "" else String(data, index, end - index, Charsets.UTF_8)

    fun skip(tag: Long) {
        when ((tag and 7).toInt()) {
            0 -> varint()
            1 -> advance(8)
            2 -> message()
            5 -> advance(4)
            else -> invalid()
        }
    }

    private fun advance(count: Int) {
        if (count > end - index) invalid()
        index += count
    }

    private fun invalid(): Nothing = throw IOException("弹幕数据格式无效")
}
