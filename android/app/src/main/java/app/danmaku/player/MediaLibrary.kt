package app.danmaku.player

import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.DocumentsContract.Document
import android.provider.OpenableColumns
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

data class BiliSource(val bvid: String, val cid: Long, val title: String, val page: Int, val duration: Double) {
    fun toJson() = JSONObject()
        .put("bvid", bvid).put("cid", cid).put("title", title).put("page", page).put("duration", duration)

    companion object {
        fun fromJson(value: JSONObject) = BiliSource(
            value.getString("bvid"), value.getLong("cid"), value.getString("title"),
            value.getInt("page"), value.getDouble("duration"),
        )
    }
}

data class CalibrationPoint(val local: Double, val source: Double)

data class MediaDetails(
    val title: String? = null,
    val year: Int? = null,
    val season: Int? = null,
    val episode: Int? = null,
    val summary: String = "",
) {
    fun toJson() = JSONObject().put("title", title).put("year", year).put("season", season)
        .put("episode", episode).put("summary", summary)

    companion object {
        fun fromJson(json: JSONObject) = MediaDetails(
            title = json.optString("title").takeIf(String::isNotBlank),
            year = json.optInt("year", -1).takeIf { it >= 0 },
            season = json.optInt("season", -1).takeIf { it >= 0 },
            episode = json.optInt("episode", -1).takeIf { it >= 0 },
            summary = json.optString("summary"),
        )
    }
}

data class ScannedMedia(
    val uri: Uri,
    val name: String,
    val details: MediaDetails = MediaDetails(),
    val duration: Double = 0.0,
    val artwork: Uri? = null,
    val subtitles: List<Uri> = emptyList(),
)

data class MediaEntry(
    val id: String = UUID.randomUUID().toString(),
    val uri: Uri,
    val name: String,
    val position: Double = 0.0,
    val duration: Double = 0.0,
    val playedAt: Long = 0,
    val source: BiliSource? = null,
    val xml: Uri? = null,
    val offset: Double = 0.0,
    val points: List<CalibrationPoint> = emptyList(),
    val imported: MediaDetails = MediaDetails(),
    val edited: MediaDetails? = null,
    val artwork: Uri? = null,
    val customArtwork: Uri? = null,
    val sidecarSubtitles: List<Uri> = emptyList(),
    val watched: Boolean = duration > 0 && position / duration > 0.95,
    val available: Boolean = true,
    val addedAt: Long = 0,
) {
    private val episodeMatch get() = episodePattern.find(name)
    private val seriesName get() = episodeMatch?.let { cleanTitle(name.substring(0, it.range.first)) }
    private val fileTitle get() = seriesName?.takeIf(String::isNotBlank) ?: cleanTitle(name.substringBeforeLast('.'))
    val seasonNumber get() = if (edited != null) edited.season
        else imported.season ?: episodeMatch?.groupValues?.get(1)?.toIntOrNull()
    val episodeNumber get() = if (edited != null) edited.episode
        else imported.episode ?: episodeMatch?.groupValues?.get(2)?.toIntOrNull()
    val episode get() = episodeNumber?.let { "S%02dE%02d".format(java.util.Locale.ROOT, seasonNumber ?: 0, it) }
    val year get() = if (edited != null) edited.year else imported.year
        ?: yearPattern.find(name)?.groupValues?.get(1)?.toIntOrNull()
    val title get() = edited?.title ?: imported.title ?: fileTitle.ifBlank { name }
    val summary get() = edited?.summary ?: imported.summary
    val poster get() = customArtwork ?: artwork
    val resumePosition get() = position.takeIf { !watched && it > 0 && duration - it > 2 } ?: 0.0
    val searchQuery get() = listOfNotNull(title, year?.toString(), episode).joinToString(" ")

    fun sourceTime(local: Double): Double {
        val point = points.lastOrNull { it.local <= local }
        return local - offset + (point?.let { it.source - it.local } ?: 0.0)
    }

    companion object {
        private val episodePattern = Regex("""S(\d{1,2})E(\d{1,3})""", RegexOption.IGNORE_CASE)
        private val yearPattern = Regex("""(?:^|[ ._(\[])(19\d{2}|20\d{2})(?=$|[ ._)\]])""")
        private val trailingAlias = Regex("""\s+[A-Za-z][A-Za-z'’.-]*(?:\s+[A-Za-z][A-Za-z'’.-]*)+$""")
        private val han = Regex("""\p{IsHan}""")

        private fun cleanTitle(name: String) = name.substring(0, yearPattern.find(name)?.range?.first ?: name.length)
            .replace('.', ' ').replace('_', ' ').trim(' ', '-')
    }

    val officialQuery: String
        get() {
            val name = title
            val base = if (han.containsMatchIn(name)) name.replace(trailingAlias, "") else name
            val season = seasonNumber
            val chinese = listOf("", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十")
            return if (season == null || season == 0 || Regex("第[^ ]*季").containsMatchIn(base)) base
            else "$base 第${chinese.getOrNull(season) ?: season}季"
        }
}

class MediaLibrary(context: Context) {
    private val data = context.getSharedPreferences("media-library", Context.MODE_PRIVATE)
    private val roots = context.getSharedPreferences("media-import-roots", Context.MODE_PRIVATE)

    fun all(): List<MediaEntry> = data.all.keys.mapNotNull(::get)

    fun get(id: String): MediaEntry? = data.getString(id, null)?.let { stored ->
        runCatching {
            val value = JSONObject(stored)
            val anchors = value.optJSONArray("points") ?: JSONArray()
            MediaEntry(
                id = id,
                uri = Uri.parse(value.getString("uri")),
                name = value.getString("name"),
                position = value.optDouble("position", 0.0),
                duration = value.optDouble("duration", 0.0),
                playedAt = value.optLong("playedAt"),
                source = value.optJSONObject("source")?.let(BiliSource::fromJson),
                xml = value.optString("xml").takeIf(String::isNotEmpty)?.let(Uri::parse),
                offset = value.optDouble("offset", 0.0),
                points = (0 until anchors.length()).map {
                    val point = anchors.getJSONArray(it)
                    CalibrationPoint(point.getDouble(0), point.getDouble(1))
                }.sortedBy(CalibrationPoint::local),
                imported = value.optJSONObject("imported")?.let(MediaDetails::fromJson) ?: MediaDetails(),
                edited = value.optJSONObject("edited")?.let(MediaDetails::fromJson),
                artwork = readUri(value, "artwork"),
                customArtwork = readUri(value, "customArtwork"),
                sidecarSubtitles = value.optJSONArray("sidecarSubtitles")?.let { urls ->
                    (0 until urls.length()).map { Uri.parse(urls.getString(it)) }
                }.orEmpty(),
                watched = value.optBoolean("watched", value.optDouble("duration", 0.0) > 0 &&
                    value.optDouble("position", 0.0) / value.optDouble("duration", 1.0) > 0.95),
                available = value.optBoolean("available", true),
                addedAt = value.optLong("addedAt"),
            )
        }.getOrNull()
    }

    fun save(entry: MediaEntry) = data.edit().putString(entry.id, encode(entry)).apply()

    fun rememberRoot(uri: Uri, folder: Boolean) = roots.edit().putBoolean(uri.toString(), folder).apply()

    fun importRoots(): Map<Uri, Boolean> {
        val remembered = roots.all.mapNotNull { (key, value) ->
            if (key == ROOTS_MIGRATED) null else (value as? Boolean)?.let { Uri.parse(key) to it }
        }.toMap().toMutableMap()
        if (roots.getBoolean(ROOTS_MIGRATED, false)) return remembered
        for (entry in all()) {
            val uri = entry.uri
            val tree = runCatching {
                DocumentsContract.buildTreeDocumentUri(uri.authority.orEmpty(), DocumentsContract.getTreeDocumentId(uri))
            }.getOrNull()
            if (tree != null) remembered[tree] = true else remembered.putIfAbsent(uri, false)
        }
        val edit = roots.edit()
        remembered.forEach { (uri, folder) -> edit.putBoolean(uri.toString(), folder) }
        edit.putBoolean(ROOTS_MIGRATED, true).apply()
        return remembered
    }

    fun forgetRoot(uri: Uri) = roots.edit().remove(uri.toString()).apply()

    fun add(files: List<ScannedMedia>): List<MediaEntry> {
        val byUri = all().associateBy { MediaFiles.documentKey(it.uri) }.toMutableMap()
        val edit = data.edit()
        val entries = files.map { file ->
            val key = MediaFiles.documentKey(file.uri)
            val old = byUri[key] ?: MediaEntry(uri = file.uri, name = file.name, addedAt = System.currentTimeMillis())
            val imported = old.imported
            val fresh = file.details
            old.copy(
                uri = if (!old.available) file.uri else old.uri,
                name = file.name,
                available = true,
                duration = old.duration.takeIf { it > 0 } ?: file.duration,
                imported = MediaDetails(fresh.title ?: imported.title, fresh.year ?: imported.year,
                    fresh.season ?: imported.season, fresh.episode ?: imported.episode,
                    fresh.summary.ifBlank { imported.summary }),
                artwork = file.artwork ?: old.artwork,
                sidecarSubtitles = file.subtitles.ifEmpty { old.sidecarSubtitles },
            ).also {
                byUri[key] = it
                edit.putString(it.id, encode(it))
            }
        }
        edit.apply()
        return entries
    }

    fun remove(id: String) = data.edit().remove(id).apply()

    fun setAvailability(status: Map<String, Boolean>) {
        val edit = data.edit()
        status.forEach { (id, available) ->
            val entry = get(id)
            if (entry != null && entry.available != available) edit.putString(id, encode(entry.copy(available = available)))
        }
        edit.apply()
    }

    private fun readUri(value: JSONObject, key: String): Uri? =
        value.optString(key).takeIf(String::isNotEmpty)?.let(Uri::parse)

    private fun encode(entry: MediaEntry): String {
        val points = JSONArray()
        entry.points.forEach { points.put(JSONArray().put(it.local).put(it.source)) }
        return JSONObject().put("uri", entry.uri.toString()).put("name", entry.name)
            .put("position", entry.position).put("duration", entry.duration).put("playedAt", entry.playedAt)
            .put("source", entry.source?.toJson()).put("xml", entry.xml?.toString())
            .put("offset", entry.offset).put("points", points).put("imported", entry.imported.toJson())
            .put("edited", entry.edited?.toJson()).put("artwork", entry.artwork?.toString())
            .put("customArtwork", entry.customArtwork?.toString())
            .put("sidecarSubtitles", JSONArray(entry.sidecarSubtitles.map(Uri::toString)))
            .put("watched", entry.watched).put("available", entry.available).put("addedAt", entry.addedAt)
            .toString()
    }

    private companion object {
        const val ROOTS_MIGRATED = "roots_migrated"
    }
}

object MediaFiles {
    private val extensions = setOf("mp4", "mkv", "mov", "avi", "webm", "m4v", "wmv", "ts", "mts", "flv", "mpg", "mpeg", "3gp", "m2ts")

    fun displayName(resolver: ContentResolver, uri: Uri): String =
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        } ?: uri.lastPathSegment?.substringAfterLast('/') ?: "视频"

    fun isVideo(name: String, mime: String?) = mime?.startsWith("video/") == true ||
        name.substringAfterLast('.', "").lowercase() in extensions

    fun documentKey(uri: Uri): String {
        val id = runCatching { DocumentsContract.getDocumentId(uri) }.getOrNull() ?: return uri.toString()
        val provider = uri.authority
        if (provider == "com.android.externalstorage.documents" && id.startsWith("primary:")) return id
        if (provider == "com.android.providers.downloads.documents" && id.startsWith("raw:")) {
            @Suppress("DEPRECATION")
            val root = android.os.Environment.getExternalStorageDirectory().path + "/"
            val path = id.removePrefix("raw:")
            if (path.startsWith(root)) return "primary:${path.removePrefix(root)}"
        }
        return "$provider/$id"
    }

    fun readable(resolver: ContentResolver, uri: Uri): Boolean =
        runCatching { resolver.openFileDescriptor(uri, "r")?.use { true } == true }.getOrDefault(false)

    fun keepAccess(resolver: ContentResolver, uri: Uri, flags: Int): Boolean {
        if (flags and Intent.FLAG_GRANT_READ_URI_PERMISSION == 0) return false
        return runCatching {
            resolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }.isSuccess
    }

    fun scan(context: Context, tree: Uri): List<ScannedMedia> {
        val resolver = context.contentResolver
        val columns = arrayOf(Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME, Document.COLUMN_MIME_TYPE)
        val pending = ArrayDeque<String>().apply { add(DocumentsContract.getTreeDocumentId(tree)) }
        val visited = mutableSetOf<String>()
        return buildList {
            while (pending.isNotEmpty()) {
                val parent = pending.removeFirst()
                if (!visited.add(parent)) continue
                val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, parent)
                val rows = resolver.query(children, columns, null, null, null) ?: continue
                val files = mutableListOf<Triple<Uri, String, String?>>()
                rows.use {
                    while (it.moveToNext()) {
                        val id = it.getString(0)
                        val name = it.getString(1)
                        if (name.startsWith('.')) continue
                        val mime = it.getString(2)
                        if (mime == Document.MIME_TYPE_DIR) pending.addLast(id)
                        else files.add(Triple(DocumentsContract.buildDocumentUriUsingTree(tree, id), name, mime))
                    }
                }
                val neighbors = files.associate { it.second.lowercase(java.util.Locale.ROOT) to it.first }
                for ((uri, name, mime) in files) {
                    if (Thread.currentThread().isInterrupted) throw InterruptedException()
                    if (isVideo(name, mime)) {
                        add(MediaMetadata.inspect(context, uri, name, neighbors))
                    }
                }
            }
        }
    }
}
