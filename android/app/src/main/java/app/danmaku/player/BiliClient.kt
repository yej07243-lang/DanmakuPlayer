package app.danmaku.player

import android.content.Context
import android.net.Uri
import android.util.AtomicFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.net.URL
import java.security.MessageDigest
import javax.net.ssl.HttpsURLConnection
import kotlin.math.ceil

data class BiliCandidate(val title: String, val detail: String, val bvid: String = "", val seasonId: Long = 0)
data class LinkSources(val pages: List<BiliSource>, val selected: BiliSource)

class BiliClient(context: Context) {
    private val app = context.applicationContext
    private val cache = File(context.filesDir, "danmaku-cache").apply { mkdirs() }
    private var cookies = ""
    private var keys: Pair<String, Long>? = null

    fun resolve(input: String): LinkSources {
        val short = shortLink.find(input)?.value?.replaceFirst("http://", "https://")
        val text = if (short != null) expand(URL(short)).toString() else input.trim()
        val episodeId = episode.find(text)?.groupValues?.get(1)?.toLongOrNull()
        if (episodeId != null) {
            val season = payload("/pgc/view/web/season", mapOf("ep_id" to episodeId.toString()), "result")
            val entries = season.optJSONArray("episodes").objects()
            val selected = entries.indexOfFirst { it.optLong("id") == episodeId }
            val source = entries.getOrNull(selected)?.let { episodeSource(it, selected + 1) }
                ?: throw IOException("番剧单集暂不可用")
            val pages = entries.mapIndexedNotNull { index, value -> episodeSource(value, index + 1) }
            return LinkSources(pages, source)
        }
        val id = bv.find(text)?.value ?: throw IOException("请输入 B 站 BV、视频链接或番剧 ep 链接")
        val rawPage = pageNumber.find(text)?.groupValues?.get(1)
        val number = if (rawPage == null) 1 else rawPage.toIntOrNull()?.takeIf { it in 1..10000 }
            ?: throw IOException("链接的分 P 编号无效")
        val pages = pages(BiliCandidate(id, "", bvid = id))
        return LinkSources(pages, pages.firstOrNull { it.page == number } ?: throw IOException("该视频没有 P$number"))
    }

    fun search(keyword: String, official: Boolean): List<BiliCandidate> {
        val mix = searchKey()
        val query = sortedMapOf(
            "search_type" to if (official) "media_ft" else "video",
            "keyword" to keyword.filterNot { it in "!'()*" },
            "page" to "1",
            "wts" to (System.currentTimeMillis() / 1000).toString(),
        )
        query["w_rid"] = MessageDigest.getInstance("MD5")
            .digest((encode(query) + mix).toByteArray()).joinToString("") { "%02x".format(it) }
        val data = payload("/x/web-interface/wbi/search/type", query)
        return data.optJSONArray("result").objects().mapNotNull { result ->
            val id = result.optString("bvid")
            val season = result.optLong("season_id")
            val title = result.optString("title").replace(Regex("<[^>]+>"), "")
                .replace("&amp;", "&").replace("&quot;", "\"")
            when {
                official && season > 0 -> BiliCandidate(title, result.optString("index_show"), seasonId = season)
                !official && bv.matches(id) -> BiliCandidate(title,
                    listOf(result.optString("author"), result.optString("duration")).filter(String::isNotBlank)
                        .joinToString(" · "), bvid = id)
                else -> null
            }
        }
    }

    fun pages(candidate: BiliCandidate): List<BiliSource> {
        return if (candidate.seasonId > 0) {
            val season = payload("/pgc/view/web/season", mapOf("season_id" to candidate.seasonId.toString()), "result")
            season.optJSONArray("episodes").objects().mapIndexedNotNull { index, row -> episodeSource(row, index + 1) }
        } else {
            val video = payload("/x/web-interface/view", mapOf("bvid" to candidate.bvid))
            video.optJSONArray("pages").objects().mapIndexedNotNull { index, row ->
                val cid = row.optLong("cid").takeIf { it > 0 } ?: return@mapIndexedNotNull null
                BiliSource(candidate.bvid, cid, row.optString("part", video.optString("title")),
                    row.optInt("page", index + 1), row.optDouble("duration", 0.0))
            }
        }
    }

    fun comments(source: BiliSource, refresh: Boolean = false): List<Danmaku> {
        if (!refresh) cachedComments(source)?.let { return it }
        val count = ceil(source.duration / 360).toInt().coerceAtLeast(1)
        if (count > 1000) throw IOException("视频时长超出弹幕加载范围")
        val entries = (1..count).flatMap { segment ->
            if (Thread.currentThread().isInterrupted) throw InterruptedException()
            val query = mapOf("type" to "1", "oid" to source.cid.toString(), "segment_index" to segment.toString())
            val data = request("/x/v2/dm/web/seg.so", query)
            val code = if (data.firstOrNull() == '{'.code.toByte()) JSONObject(data.toString(Charsets.UTF_8)).optInt("code") else 0
            if (code != 0) throw IOException("B 站弹幕访问受限（$code），可导入本地 XML")
            val comments = DanmakuParser.protobuf(data)
            if (segment < count) Thread.sleep(150)
            comments
        }.distinctBy(Danmaku::id).sortedBy(Danmaku::time)
        val rows = JSONArray()
        entries.forEach { rows.put(JSONArray().put(it.id).put(it.time).put(it.mode).put(it.color).put(it.text)) }
        if (Thread.currentThread().isInterrupted) throw InterruptedException()
        val file = cacheFile(source)
        val output = file.startWrite()
        try {
            output.write(rows.toString().toByteArray())
            file.finishWrite(output)
        } catch (error: IOException) {
            file.failWrite(output)
            throw error
        }
        Diagnostics.record(app, TAG, "Downloaded ${entries.size} comments in $count segments (cid=${source.cid})")
        return entries
    }

    fun cachedComments(source: BiliSource): List<Danmaku>? {
        val files = listOf(cacheFile(source), legacyCacheFile(source))
        return files.firstNotNullOfOrNull { file ->
            runCatching {
                val rows = JSONArray(file.readFully().toString(Charsets.UTF_8))
                (0 until rows.length()).map { index ->
                    val row = rows.getJSONArray(index)
                    Danmaku(row.getString(0), row.getDouble(1), row.getInt(2), row.getInt(3), row.getString(4))
                }
            }.getOrNull()?.also {
                Diagnostics.record(app, TAG, "Loaded ${it.size} comments from cache (cid=${source.cid})")
            }
        }
    }

    fun clearCache(source: BiliSource) {
        cacheFile(source).delete()
        legacyCacheFile(source).delete()
    }

    private fun cacheFile(source: BiliSource): AtomicFile {
        val key = source.bvid.takeIf(bv::matches) ?: "bili"
        return AtomicFile(File(cache, "$key-${source.cid}.json"))
    }

    private fun legacyCacheFile(source: BiliSource) = AtomicFile(File(cache, "${source.cid}.json"))

    private fun episodeSource(entry: JSONObject, page: Int): BiliSource? {
        val id = entry.optString("bvid").takeIf(bv::matches) ?: return null
        val cid = entry.optLong("cid").takeIf { it > 0 } ?: return null
        val title = "第${entry.optString("title", page.toString())}集 ${entry.optString("long_title")}".trim()
        return BiliSource(id, cid, title, page, entry.optDouble("duration", 0.0) / 1000)
    }

    private fun searchKey(): String {
        val now = System.currentTimeMillis()
        keys?.takeIf { now - it.second < 21_600_000 }?.let { return it.first }
        val guest = payload("/x/frontend/finger/spi")
        cookies = listOf("b_3" to "buvid3", "b_4" to "buvid4").mapNotNull { (field, cookie) ->
            guest.optString(field).takeIf { it.matches(Regex("[A-Za-z0-9_=-]+")) }?.let { "$cookie=$it" }
        }.joinToString("; ")
        val nav = JSONObject(request("/x/web-interface/nav").toString(Charsets.UTF_8))
        val images = nav.optJSONObject("data")?.optJSONObject("wbi_img")
            ?: throw IOException("搜索暂不可用，可粘贴视频链接")
        val raw = listOf("img_url", "sub_url").joinToString("") { key ->
            Uri.parse(images.optString(key)).lastPathSegment.orEmpty().substringBeforeLast('.')
        }
        if (!raw.matches(Regex("[0-9a-f]{64}"))) throw IOException("B 站搜索签名无效")
        val order = intArrayOf(46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13)
        val mix = order.map(raw::get).joinToString("")
        keys = mix to now
        return mix
    }

    private fun payload(path: String, query: Map<String, String> = emptyMap(), key: String = "data"): JSONObject {
        val json = JSONObject(request(path, query).toString(Charsets.UTF_8))
        val code = json.optInt("code", -1)
        if (code != 0) throw IOException("B 站请求失败（$code）\n${json.optString("message")}")
        return (json.optJSONObject(key) ?: throw IOException("B 站返回的数据不完整")).also {
            if (it.has("v_voucher")) throw IOException("B 站搜索需要网页验证，可粘贴完整视频链接")
        }
    }

    private fun request(path: String, query: Map<String, String> = emptyMap()): ByteArray {
        val parameters = if (query.isEmpty()) "" else "?${encode(query)}"
        val connection = connection(URL("https://api.bilibili.com$path$parameters"))
        return try {
            if (cookies.isNotEmpty()) connection.setRequestProperty("Cookie", cookies)
            val code = connection.responseCode
            if (code != 200) {
                Diagnostics.record(app, TAG, "Endpoint $path returned HTTP $code")
                throw IOException("B 站请求失败（HTTP $code）")
            }
            connection.inputStream.use { it.readLimited(16_000_000) }
        } finally {
            connection.disconnect()
        }
    }

    private fun expand(start: URL): URL {
        var url = start
        repeat(8) {
            val host = url.host.lowercase()
            if (url.protocol != "https" || url.userInfo != null || url.port !in setOf(-1, 443) ||
                (host != "b23.tv" && host != "www.b23.tv" && host != "bilibili.com" && !host.endsWith(".bilibili.com"))) {
                throw IOException("短链接指向了非 B 站地址")
            }
            if (host == "bilibili.com" || host.endsWith(".bilibili.com")) {
                if (bv.containsMatchIn(url.toString()) || episode.containsMatchIn(url.toString())) return url
            }
            val connection = connection(url)
            try {
                connection.requestMethod = "HEAD"
                val code = connection.responseCode
                val location = connection.getHeaderField("Location")
                if (code !in 300..399 || location.isNullOrBlank()) throw IOException("短链接无法解析，请粘贴完整链接")
                url = URL(url, location)
            } finally {
                connection.disconnect()
            }
        }
        throw IOException("短链接跳转次数过多")
    }

    private fun connection(url: URL) = (url.openConnection() as HttpsURLConnection).apply {
        instanceFollowRedirects = false
        connectTimeout = 10_000
        readTimeout = 15_000
        setRequestProperty("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36")
        setRequestProperty("Referer", "https://www.bilibili.com/")
    }

    private fun encode(query: Map<String, String>) = query.entries.joinToString("&") {
        "${Uri.encode(it.key)}=${Uri.encode(it.value)}"
    }

    private fun JSONArray?.objects(): List<JSONObject> =
        if (this == null) emptyList() else (0 until length()).mapNotNull(::optJSONObject)

    companion object {
        private const val TAG = "DanmakuBili"
        private val bv = Regex("BV[0-9A-Za-z]{10}")
        private val episode = Regex("""(?:^|/)ep([0-9]+)(?=[/?#]|$)""")
        private val pageNumber = Regex("""[?&]p=([^&#\s]*)""")
        private val shortLink = Regex("""https?://(?:www\.)?b23\.tv/[A-Za-z0-9]+""")
    }
}

internal fun InputStream.readLimited(limit: Int): ByteArray {
    val output = ByteArrayOutputStream()
    val buffer = ByteArray(8192)
    while (true) {
        val count = read(buffer)
        if (count < 0) return output.toByteArray()
        if (count > limit - output.size()) throw IOException("文件或响应超过大小限制")
        output.write(buffer, 0, count)
    }
}
