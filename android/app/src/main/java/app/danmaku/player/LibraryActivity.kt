package app.danmaku.player

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.text.TextWatcher
import android.text.Editable
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.BaseAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.FrameLayout
import android.widget.PopupMenu
import android.widget.ProgressBar
import android.widget.ListView
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.util.Locale
import java.util.concurrent.Executors

class LibraryActivity : Activity() {
    private lateinit var library: MediaLibrary
    private lateinit var search: EditText
    private lateinit var filters: List<Button>
    private lateinit var empty: TextView
    private lateinit var libraryPane: LinearLayout
    private lateinit var detailPane: LinearLayout
    private lateinit var detailHeader: LinearLayout
    private lateinit var detailTitle: TextView
    private lateinit var details: ScrollView
    private lateinit var split: LinearLayout
    private lateinit var rows: ListView
    private lateinit var episodes: ListView
    private lateinit var adapter: PosterAdapter
    private lateinit var episodeAdapter: PosterAdapter
    private var mode = 0
    private var series: String? = null
    private var selectedId: String? = null
    private var wide = false
    private var relinking: String? = null
    private var scanning = false
    private var recentFirst = false
    private var coverEntry: String? = null
    private var playlist = emptyList<String>()
    private val worker = Executors.newSingleThreadExecutor()
    private val posters = Executors.newSingleThreadExecutor()
    private val artworkCache = object : android.util.LruCache<Uri, Bitmap>(6 * 1024 * 1024) {
        override fun sizeOf(key: Uri, value: Bitmap) = value.allocationByteCount
    }
    private val filterLabels = listOf("全部", "继续观看", "剧集", "电影", "文件不可用")

    private data class Row(
        val key: String,
        val title: String,
        val subtitle: String = "",
        val entry: MediaEntry? = null,
        val series: String? = null,
        val poster: Uri? = entry?.poster,
        val heading: Boolean = false,
    )

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        library = MediaLibrary(this)
        library.importRoots()
        relinking = savedInstanceState?.getString("relinking")
        coverEntry = savedInstanceState?.getString("coverEntry")
        mode = savedInstanceState?.getInt("mode") ?: 0
        series = savedInstanceState?.getString("series")
        selectedId = savedInstanceState?.getString("selectedId")
        recentFirst = savedInstanceState?.getBoolean("recentFirst") ?: false
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            applySystemInsets(this, includeIme = true)
            isFocusableInTouchMode = true
        }
        split = LinearLayout(this)
        libraryPane = createLibraryPane(savedInstanceState?.getString("search"))
        detailPane = createDetailPane()
        split.addView(libraryPane, LinearLayout.LayoutParams(0, -1, 1f))
        split.addView(detailPane, LinearLayout.LayoutParams(0, -1, 1f).apply { marginStart = dp(8) })
        root.addView(split, LinearLayout.LayoutParams(-1, 0, 1f))
        split.addOnLayoutChangeListener { _, left, _, right, _, _, _, _, _ ->
            val nextWide = right - left >= dp(720)
            if (wide != nextWide) {
                wide = nextWide
                updatePanes()
            }
        }
        setContentView(root)
        root.requestFocus()
        search.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                clearSelection()
                updateList(keepPosition = false)
            }
            override fun afterTextChanged(s: Editable?) = Unit
        })
        if (Build.VERSION.SDK_INT >= 33) onBackInvokedDispatcher.registerOnBackInvokedCallback(0, ::goBack)
        updateList()
        rows.setSelectionFromTop(savedInstanceState?.getInt("libraryPosition") ?: 0,
            savedInstanceState?.getInt("libraryTop") ?: 0)
        episodes.setSelectionFromTop(savedInstanceState?.getInt("episodePosition") ?: 0,
            savedInstanceState?.getInt("episodeTop") ?: 0)
    }

    private fun createLibraryPane(query: String?) = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(16), dp(8), dp(16), 0)
        addView(LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            addView(heading("媒体库", 28f), LinearLayout.LayoutParams(0, -2, 1f))
            val import = uiButton("导入") { }
            import.setOnClickListener { showImport(import) }
            selectButton(import, true)
            import.isSelected = false
            addView(import)
            addView(uiButton("⋮", "媒体库选项") { showLibraryOptions() })
        })
        search = EditText(context).apply {
            hint = "搜索视频或剧集"
            setSingleLine()
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            imeOptions = EditorInfo.IME_ACTION_SEARCH or EditorInfo.IME_FLAG_NO_EXTRACT_UI
            minHeight = dp(48)
            setPadding(dp(14), dp(8), dp(14), dp(8))
            background = rounded(themeColor(android.R.attr.colorBackgroundFloating))
            setText(query)
            setOnEditorActionListener { _, action, _ ->
                if (action != EditorInfo.IME_ACTION_SEARCH) return@setOnEditorActionListener false
                getSystemService(InputMethodManager::class.java)
                    .hideSoftInputFromWindow(windowToken, 0)
                clearFocus()
                true
            }
        }
        addView(search, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8); bottomMargin = dp(8) })
        filters = filterLabels.mapIndexed { index, label ->
            uiButton(label) {
                if (mode != index) {
                    mode = index
                    clearSelection()
                    updateList(keepPosition = false)
                }
            }
        }
        addView(buttonStrip(*filters.toTypedArray()), LinearLayout.LayoutParams(-1, -2))
        empty = TextView(context).apply {
            setTextColor(themeColor(android.R.attr.textColorSecondary))
            gravity = Gravity.CENTER
            setPadding(dp(8), dp(20), dp(8), dp(12))
            accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        addView(empty)
        adapter = PosterAdapter()
        rows = createRows(adapter, ::selectRow)
        addView(rows, LinearLayout.LayoutParams(-1, 0, 1f))
    }

    private fun createDetailPane() = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(16), dp(8), dp(16), 0)
        detailHeader = LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            addView(uiButton("‹", "返回媒体库列表") { clearSelection(); updateList() })
            detailTitle = heading("")
            addView(detailTitle, LinearLayout.LayoutParams(0, -2, 1f))
        }
        addView(detailHeader)
        episodeAdapter = PosterAdapter()
        episodes = createRows(episodeAdapter, ::selectRow)
        addView(episodes, LinearLayout.LayoutParams(-1, 0, 1f))
        details = ScrollView(context).apply { isFillViewport = true }
        addView(details, LinearLayout.LayoutParams(-1, 0, 1f))
    }

    private fun createRows(posterAdapter: PosterAdapter, select: (Row) -> Unit) = ListView(this).apply {
        adapter = posterAdapter
        divider = null
        setItemsCanFocus(true)
        clipToPadding = false
        setPadding(0, dp(8), 0, dp(8))
        setOnItemClickListener { _, _, index, _ -> select(posterAdapter.getItem(index)) }
        setOnItemLongClickListener { _, _, index, _ ->
            posterAdapter.getItem(index).entry?.let { actions(it); true } ?: false
        }
    }

    private fun showImport(anchor: View) {
        PopupMenu(this, anchor).apply {
            menu.add(0, IMPORT, 0, "视频")
            menu.add(0, FOLDER, 1, "文件夹")
            setOnMenuItemClickListener { item ->
                if (item.itemId == IMPORT) pickFiles(IMPORT, multiple = true)
                else pick(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE), FOLDER)
                true
            }
            show()
        }
    }

    private fun selectRow(row: Row) {
        row.series?.let {
            series = it
            selectedId = null
            updateList()
            episodes.setSelection(0)
            return
        }
        val entry = row.entry ?: return
        if (wide && series == null) {
            selectedId = entry.id
            updateList()
        } else open(entry)
    }

    private fun clearSelection() {
        series = null
        selectedId = null
    }

    private fun goBack() {
        if (series != null || selectedId != null) {
            clearSelection()
            updateList()
        } else finish()
    }

    @Deprecated("Used before API 33.")
    override fun onBackPressed() = goBack()

    override fun onResume() {
        super.onResume()
        updateList()
        val entries = library.all()
        if (entries.isNotEmpty() && !scanning) worker.submit {
            val status = entries.associate { it.id to MediaFiles.readable(contentResolver, it.uri) }
            val paths = entries.associate { it.id to it.uri }
            runOnUiThread {
                if (!isDestroyed && !scanning) {
                    library.setAvailability(status.filterKeys { id -> library.get(id)?.uri == paths[id] })
                    updateList()
                }
            }
        }
    }

    private fun updateList(keepPosition: Boolean = true) {
        filters.forEachIndexed { index, button -> selectButton(button, mode == index) }
        val query = search.text.toString().trim()
        val all = library.all()
        val matches = all.filter { entry ->
            (entry.name.contains(query, ignoreCase = true) || entry.title.contains(query, ignoreCase = true)) && when (mode) {
                1 -> entry.available && entry.resumePosition > 0
                2 -> entry.episode != null
                3 -> entry.episode == null
                4 -> !entry.available
                else -> true
            }
        }
        val ordered = if (mode == 1 || recentFirst) matches.sortedByDescending { maxOf(it.playedAt, it.addedAt) }
        else matches.sortedWith(compareBy<MediaEntry> { it.title.lowercase(Locale.ROOT) }
            .thenBy(MediaEntry::seasonNumber).thenBy(MediaEntry::episodeNumber).thenBy(MediaEntry::name))
        val grouped = mode == 0 || mode == 2
        val renderedGroups = mutableSetOf<String>()
        val bySeries = ordered.filter { it.episode != null }.groupBy { it.title.lowercase(Locale.ROOT) }
        val listRows = ordered.mapNotNull { entry ->
            if (!grouped || entry.episode == null) return@mapNotNull entryRow(entry)
            val key = entry.title.lowercase(Locale.ROOT)
            if (!renderedGroups.add(key)) return@mapNotNull null
            val episodes = bySeries.getValue(key)
            Row("series:$key", entry.title, "${episodes.size} 集", series = key,
                poster = episodes.firstNotNullOfOrNull(MediaEntry::poster))
        }
        val group = series?.let(bySeries::get).orEmpty().sortedWith(
            compareBy<MediaEntry> { it.seasonNumber }.thenBy { it.episodeNumber }.thenBy { it.name },
        )
        if (group.isEmpty()) series = null
        val selected = selectedId?.let { id -> ordered.firstOrNull { it.id == id } }
        if (selected == null) selectedId = null
        playlist = (if (series == null) ordered else group).filter(MediaEntry::available).map(MediaEntry::id)
        val episodeRows = group.groupBy(MediaEntry::seasonNumber).flatMap { (season, items) ->
            val heading = Row("season:$season", season?.let { "第 $it 季" } ?: "剧集", heading = true)
            listOf(heading) + items.map { entryRow(it, showSeries = false) }
        }
        refreshRows(rows, adapter, listRows, keepPosition)
        refreshRows(episodes, episodeAdapter, episodeRows, keepPosition)
        detailTitle.text = group.firstOrNull()?.title ?: selected?.title.orEmpty()
        if (selected != null) renderDetails(selected)
        empty.visibility = if (scanning || listRows.isEmpty()) View.VISIBLE else View.GONE
        empty.text = when {
            scanning -> "正在扫描文件夹…"
            all.isEmpty() -> "导入视频或文件夹开始观看"
            else -> "没有符合条件的视频"
        }
        updatePanes()
    }

    private fun refreshRows(list: ListView, posterAdapter: PosterAdapter, items: List<Row>, keepPosition: Boolean) {
        val first = list.firstVisiblePosition
        val top = list.getChildAt(0)?.top ?: 0
        val key = posterAdapter.items.getOrNull(first)?.key
        posterAdapter.items = items
        posterAdapter.notifyDataSetChanged()
        if (items.isEmpty()) return
        val index = if (keepPosition) items.indexOfFirst { it.key == key }.takeIf { it >= 0 }
            ?: first.coerceAtMost(items.lastIndex) else 0
        list.setSelectionFromTop(index, if (keepPosition && key != null) top - list.paddingTop else 0)
    }

    private fun updatePanes() {
        val showingDetail = series != null || selectedId != null
        libraryPane.visibility = if (wide || !showingDetail) View.VISIBLE else View.GONE
        detailPane.visibility = if (wide || showingDetail) View.VISIBLE else View.GONE
        val detailParams = detailPane.layoutParams as LinearLayout.LayoutParams
        val detailMargin = if (wide) dp(8) else 0
        if (detailParams.marginStart != detailMargin) {
            detailParams.marginStart = detailMargin
            detailPane.layoutParams = detailParams
        }
        detailHeader.visibility = if (series != null || selectedId != null) View.VISIBLE else View.GONE
        episodes.visibility = if (series != null) View.VISIBLE else View.GONE
        details.visibility = if (series == null) View.VISIBLE else View.GONE
        if (wide && series == null && selectedId == null) {
            details.removeAllViews()
            details.addView(TextView(this).apply {
                text = "选择影片或剧集"
                gravity = Gravity.CENTER
                setTextColor(themeColor(android.R.attr.textColorSecondary))
            })
        }
    }

    private fun entryRow(entry: MediaEntry, showSeries: Boolean = true): Row {
        val label = if (showSeries) listOfNotNull(entry.title, entry.episode ?: entry.year?.toString()).joinToString("  ")
        else entry.episode ?: entry.title
        val status = when {
            !entry.available -> "文件不可用"
            entry.watched -> "已看完"
            entry.resumePosition > 0 -> "${playbackTime(entry.position)} / ${playbackTime(entry.duration)}"
            entry.duration > 0 -> playbackTime(entry.duration)
            else -> null
        }
        val source = when {
            entry.xml != null -> "本地 XML"
            entry.source != null -> "B 站 · ${entry.source.title.ifBlank { entry.source.bvid }}"
            else -> null
        }
        return Row(entry.id, label, listOfNotNull(status, source).joinToString(" · "), entry = entry)
    }

    private fun renderDetails(entry: MediaEntry) {
        val position = details.scrollY
        val sameEntry = details.tag == entry.id
        details.tag = entry.id
        details.removeAllViews()
        details.addView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(8), dp(8), dp(8), dp(12))
            addView(TextView(context).apply {
                text = detailFields(entry).joinToString("\n\n")
                textSize = 16f
                setTextIsSelectable(true)
            })
            addView(uiButton(playLabel(entry)) { if (entry.available) open(entry) else relink(entry) })
            if (entry.available) addView(uiButton("关联弹幕") { open(entry, sourceMenu = true) })
            addView(uiButton("更多影片操作") { actions(entry) })
        })
        details.post { details.scrollTo(0, if (sameEntry) position else 0) }
    }

    private fun open(entry: MediaEntry, sourceMenu: Boolean = false) {
        if (!MediaFiles.readable(contentResolver, entry.uri)) {
            library.save(entry.copy(available = false))
            updateList()
            AlertDialog.Builder(this).setTitle("文件不可用").setMessage(entry.name)
                .setPositiveButton("重新定位") { _, _ -> relink(entry) }
                .setNegativeButton("取消", null).show()
            return
        }
        if (!entry.available) library.save(entry.copy(available = true))
        startActivity(Intent(this, MainActivity::class.java).putExtra(MainActivity.ENTRY_ID, entry.id)
            .putExtra(MainActivity.SOURCE_MENU, sourceMenu)
            .putStringArrayListExtra(MainActivity.PLAYLIST, ArrayList(playlist)))
    }

    private fun actions(entry: MediaEntry) {
        AlertDialog.Builder(this).setTitle(entry.title)
            .setItems(arrayOf("影片详情", "编辑资料", "更换海报", "从头播放", "重新定位文件", "从媒体库移除")) { _, choice ->
                when (choice) {
                    0 -> showDetails(entry)
                    1 -> editDetails(entry)
                    2 -> {
                        coverEntry = entry.id
                        pick(Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
                            addCategory(Intent.CATEGORY_OPENABLE)
                            type = "image/*"
                        }, COVER)
                    }
                    3 -> {
                        val reset = entry.copy(position = 0.0, watched = false)
                        library.save(reset)
                        updateList()
                        open(reset)
                    }
                    4 -> relink(entry)
                    5 -> AlertDialog.Builder(this).setMessage("从媒体库移除这条记录？原文件会保留。")
                        .setPositiveButton("移除") { _, _ ->
                            library.remove(entry.id)
                            MediaMetadata.deleteCopiedArtwork(this, entry.customArtwork)
                            LocalDanmaku.delete(this, entry.xml)
                            updateList()
                        }
                        .setNegativeButton("取消", null).show()
                }
            }.show()
    }

    private fun detailFields(entry: MediaEntry) = listOfNotNull(
        entry.year?.toString(), entry.episode, entry.duration.takeIf { it > 0 }?.let(::playbackTime),
        entry.summary.takeIf(String::isNotBlank), entry.name,
        entry.source?.let { "B 站 · ${it.title.ifBlank { it.bvid }}" } ?: entry.xml?.let { "本地弹幕 XML" },
        "继续观看 · ${playbackTime(entry.position)}".takeIf { entry.resumePosition > 0 },
        "文件不可用".takeUnless { entry.available },
    )

    private fun playLabel(entry: MediaEntry) = when {
        !entry.available -> "重新定位"
        entry.resumePosition > 0 -> "继续播放"
        else -> "播放"
    }

    private fun showDetails(entry: MediaEntry) {
        AlertDialog.Builder(this).setTitle(entry.title).setMessage(detailFields(entry).joinToString("\n\n"))
            .setPositiveButton(playLabel(entry)) { _, _ -> if (entry.available) open(entry) else relink(entry) }
            .setNeutralButton("关联弹幕") { _, _ -> open(entry, sourceMenu = true) }
            .setNegativeButton("关闭", null).show()
    }

    private fun editDetails(entry: MediaEntry) {
        val form = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), 0, dp(20), 0) }
        fun field(label: String, initial: String, number: Boolean = false, multiline: Boolean = false) =
            EditText(this).apply {
                form.addView(TextView(this@LibraryActivity).apply { text = label })
                setText(initial)
                if (number) inputType = InputType.TYPE_CLASS_NUMBER
                if (multiline) minLines = 2 else setSingleLine()
                form.addView(this)
            }
        val title = field("片名", entry.title)
        val year = field("年份", entry.year?.toString().orEmpty(), number = true)
        val season = field("季", entry.seasonNumber?.toString().orEmpty(), number = true)
        val episode = field("集", entry.episodeNumber?.toString().orEmpty(), number = true)
        val summary = field("简介", entry.summary, multiline = true)
        val watched = android.widget.CheckBox(this).apply { text = "已看完"; isChecked = entry.watched; form.addView(this) }
        val dialog = AlertDialog.Builder(this).setTitle("编辑资料").setView(ScrollView(this).apply { addView(form) })
            .setNegativeButton("取消", null).setPositiveButton("保存", null).show()
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            if (title.text.isBlank()) {
                title.error = "请输入片名"
                return@setOnClickListener
            }
            for (field in listOf(year, season, episode)) {
                if (field.text.isNotBlank() && field.text.toString().toIntOrNull()?.let { it >= 0 } != true) {
                    field.error = "请输入有效数字"
                    return@setOnClickListener
                }
            }
            val edited = MediaDetails(title.text.toString().trim(), year.text.toString().toIntOrNull(),
                season.text.toString().toIntOrNull(), episode.text.toString().toIntOrNull(), summary.text.toString().trim())
            val current = library.get(entry.id) ?: return@setOnClickListener
            val position = if (current.watched && !watched.isChecked && current.duration > 0 &&
                current.position / current.duration > 0.95) 0.0 else current.position
            library.save(current.copy(edited = edited, watched = watched.isChecked, position = position))
            updateList()
            dialog.dismiss()
        }
    }

    private fun showLibraryOptions() {
        AlertDialog.Builder(this).setTitle("媒体库选项")
            .setItems(arrayOf(if (recentFirst) "按标题排序" else "按最近播放排序", "重新扫描",
                "管理导入位置", "导出诊断日志", "版本信息")) { _, choice ->
                when (choice) {
                    0 -> { recentFirst = !recentFirst; updateList() }
                    1 -> scan(library.importRoots())
                    2 -> showImportRoots()
                    3 -> pick(Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
                        addCategory(Intent.CATEGORY_OPENABLE)
                        type = "text/plain"
                        putExtra(Intent.EXTRA_TITLE, "DanmakuPlayer-diagnostics.txt")
                    }, DIAGNOSTICS)
                    4 -> {
                        val version = packageManager.getPackageInfo(packageName, 0).versionName
                        AlertDialog.Builder(this).setTitle("DanmakuPlayer").setMessage("Android $version")
                            .setPositiveButton("关闭", null).show()
                    }
                }
            }.show()
    }

    private fun showImportRoots() {
        val roots = library.importRoots().entries.toList()
        if (roots.isEmpty()) {
            toast("还没有保存的导入位置")
            return
        }
        val names = roots.map { (uri, tree) ->
            val path = runCatching { if (tree) android.provider.DocumentsContract.getTreeDocumentId(uri)
                else MediaFiles.displayName(contentResolver, uri) }.getOrDefault(uri.lastPathSegment.orEmpty())
            (if (tree) "文件夹 · " else "视频 · ") + path.substringAfter("primary:")
        }
        AlertDialog.Builder(this).setTitle("管理导入位置")
            .setItems(names.toTypedArray()) { _, index ->
                AlertDialog.Builder(this).setTitle("取消重扫此位置？")
                    .setMessage(names[index])
                    .setPositiveButton("取消重扫") { _, _ -> library.forgetRoot(roots[index].key) }
                    .setNegativeButton("返回", null).show()
            }.show()
    }

    private fun scan(roots: Map<Uri, Boolean>) {
        if (scanning) {
            toast("正在扫描")
            return
        }
        if (roots.isEmpty()) {
            toast("还没有保存的导入位置")
            return
        }
        scanning = true
        updateList()
        worker.submit {
            var failures = 0
            val found = roots.flatMap { (uri, folder) ->
                runCatching {
                    if (folder) MediaFiles.scan(this, uri) else {
                        if (!MediaFiles.readable(contentResolver, uri)) throw java.io.IOException()
                        listOf(MediaMetadata.inspect(this, uri, MediaFiles.displayName(contentResolver, uri)))
                    }
                }.getOrElse { failures++; emptyList() }
            }
            val checked = library.all().associate { it.id to (it.uri to MediaFiles.readable(contentResolver, it.uri)) }
            val result = "扫描完成 · ${found.size} 个视频" + if (failures > 0) " · $failures 个位置无法读取" else ""
            Diagnostics.record(this, "DanmakuLibrary", "Scan matched ${found.size} videos, unavailable roots: $failures")
            runOnUiThread {
                if (isDestroyed) return@runOnUiThread
                library.setAvailability(checked.mapNotNull { (id, status) ->
                    if (library.get(id)?.uri == status.first) id to status.second else null
                }.toMap())
                library.add(found)
                scanning = false
                updateList()
                toast(result)
            }
        }
    }

    private fun relink(entry: MediaEntry) {
        relinking = entry.id
        pickFiles(RELINK)
    }

    private fun pickFiles(request: Int, multiple: Boolean = false) {
        pick(Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE)
            type = "*/*"
            putExtra(Intent.EXTRA_ALLOW_MULTIPLE, multiple)
        }, request)
    }

    @Suppress("DEPRECATION")
    private fun pick(intent: Intent, request: Int) {
        try {
            startActivityForResult(intent, request)
        } catch (_: android.content.ActivityNotFoundException) {
            toast("系统文件选择器不可用")
        }
    }

    @Deprecated("The player uses platform document pickers.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, result: Intent?) {
        super.onActivityResult(requestCode, resultCode, result)
        if (resultCode != RESULT_OK || result == null) return
        val uri = result.data
        if (uri != null && requestCode == DIAGNOSTICS) {
            worker.submit {
                val exported = runCatching {
                    contentResolver.openOutputStream(uri, "wt")?.use { it.write(Diagnostics.snapshot(this)) }
                        ?: throw java.io.IOException()
                }.isSuccess
                runOnUiThread { if (!isDestroyed) toast(if (exported) "已导出诊断日志" else "无法写入诊断日志") }
            }
            return
        }
        if (uri != null && requestCode == COVER) {
            val id = coverEntry ?: return
            coverEntry = null
            worker.submit {
                val artwork = runCatching { MediaMetadata.copyArtwork(this, uri) }.getOrNull()
                runOnUiThread {
                    val entry = library.get(id)
                    if (artwork != null && entry != null && !isDestroyed) {
                        library.save(entry.copy(customArtwork = artwork))
                        MediaMetadata.deleteCopiedArtwork(this, entry.customArtwork)
                        updateList()
                    } else {
                        MediaMetadata.deleteCopiedArtwork(this, artwork)
                        if (!isDestroyed) toast("无法保存这张海报")
                    }
                }
            }
            return
        }
        if (requestCode == FOLDER && uri != null) {
            if (!MediaFiles.keepAccess(contentResolver, uri, result.flags)) {
                toast("无法保留文件夹的读取权限")
                return
            }
            library.rememberRoot(uri, true)
            scan(mapOf(uri to true))
            return
        }
        val selection = result.clipData?.let { clip -> (0 until clip.itemCount).map { clip.getItemAt(it).uri } }
            ?: listOfNotNull(uri)
        val videos = selection.mapNotNull { file ->
            runCatching {
                val name = MediaFiles.displayName(contentResolver, file)
                if (MediaFiles.isVideo(name, contentResolver.getType(file)) &&
                    MediaFiles.keepAccess(contentResolver, file, result.flags)) file to name else null
            }.getOrNull()
        }
        if (videos.isEmpty()) {
            toast("请选择可读取的本地视频")
            return
        }
        if (requestCode == RELINK) {
            val old = relinking?.let(library::get) ?: return
            val (file, name) = videos.first()
            if (library.all().any { MediaFiles.documentKey(it.uri) == MediaFiles.documentKey(file) && it.id != old.id }) {
                toast("这个视频已经在媒体库中")
                return
            }
            library.save(old.copy(uri = file, name = name, available = true, sidecarSubtitles = emptyList()))
            library.rememberRoot(file, false)
            relinking = null
        } else if (requestCode == IMPORT) {
            videos.forEach { (file, _) -> library.rememberRoot(file, false) }
            scan(videos.associate { it.first to false })
        }
        updateList()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putString("relinking", relinking)
        outState.putString("coverEntry", coverEntry)
        outState.putString("search", search.text.toString())
        outState.putInt("mode", mode)
        outState.putString("series", series)
        outState.putString("selectedId", selectedId)
        outState.putInt("libraryPosition", rows.firstVisiblePosition)
        outState.putInt("libraryTop", (rows.getChildAt(0)?.top ?: rows.paddingTop) - rows.paddingTop)
        outState.putInt("episodePosition", episodes.firstVisiblePosition)
        outState.putInt("episodeTop", (episodes.getChildAt(0)?.top ?: episodes.paddingTop) - episodes.paddingTop)
        outState.putBoolean("recentFirst", recentFirst)
        super.onSaveInstanceState(outState)
    }

    override fun onDestroy() {
        worker.shutdownNow()
        posters.shutdownNow()
        super.onDestroy()
    }

    private inner class PosterAdapter : BaseAdapter() {
        var items = emptyList<Row>()

        override fun getCount() = items.size
        override fun getItem(position: Int) = items[position]
        override fun getItemId(position: Int) = position.toLong()
        override fun getViewTypeCount() = 2
        override fun getItemViewType(position: Int) = if (getItem(position).heading) 1 else 0
        override fun areAllItemsEnabled() = false
        override fun isEnabled(position: Int) = !getItem(position).heading

        override fun getView(position: Int, convertView: View?, parent: ViewGroup?): View {
            val row = getItem(position)
            if (row.heading) return ((convertView as? TextView) ?: heading("", 17f).apply {
                setPadding(dp(4), dp(14), dp(4), dp(6))
            }).apply { text = row.title }
            return ((convertView as? PosterCell) ?: PosterCell()).apply { bind(row) }
        }
    }

    private inner class PosterCell : FrameLayout(this@LibraryActivity) {
        private val placeholder = TextView(context).apply {
            gravity = Gravity.CENTER
            textSize = 21f
            setTextColor(themeColor(android.R.attr.colorAccent))
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }
        private val art = ImageView(context).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }
        private val artwork = FrameLayout(context).apply {
            background = rounded(themeColor(android.R.attr.colorBackground), 8)
            clipToOutline = true
            addView(placeholder, LayoutParams(-1, -1))
            addView(art, LayoutParams(-1, -1))
        }
        private val title = TextView(context).apply {
            textSize = 17f
            setTypeface(typeface, android.graphics.Typeface.BOLD)
        }
        private val subtitle = TextView(context).apply {
            textSize = 14f
            setTextColor(themeColor(android.R.attr.textColorSecondary))
        }
        private val progress = ProgressBar(context, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 1000
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }
        private var bound: Row? = null
        private val more = uiButton("⋮", "更多") {
            bound?.entry?.let(::actions)
        }
        private val card = LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(10), dp(8), dp(4), dp(8))
            addView(artwork, LinearLayout.LayoutParams(dp(46), dp(62)))
            addView(LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(12), 0, 0, 0)
                addView(title)
                addView(subtitle)
                addView(progress, LinearLayout.LayoutParams(-1, dp(8)).apply { topMargin = dp(4) })
            }, LinearLayout.LayoutParams(0, -2, 1f))
            addView(more)
        }

        init {
            addView(card, LayoutParams(-1, -2).apply { bottomMargin = dp(8) })
        }

        fun bind(row: Row) {
            bound = row
            val entry = row.entry
            val selected = row.series?.let { it == series } ?: (entry?.id == selectedId)
            isSelected = selected
            val color = if (selected) (themeColor(android.R.attr.colorAccent) and 0x00ffffff) or 0x28000000
                else themeColor(android.R.attr.colorBackgroundFloating)
            card.background = ripple(color)
            card.contentDescription = listOf(row.title, row.subtitle).filter(String::isNotBlank).joinToString("，")
            card.isFocusable = true
            card.setOnClickListener { bound?.let(::selectRow) }
            card.setOnLongClickListener { bound?.entry?.let { actions(it); true } ?: false }
            title.importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            subtitle.importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            title.text = row.title
            subtitle.text = row.subtitle
            subtitle.visibility = if (row.subtitle.isBlank()) View.GONE else View.VISIBLE
            placeholder.text = row.title.take(1)
            more.visibility = if (entry == null) View.INVISIBLE else View.VISIBLE
            more.contentDescription = "更多，${row.title}"
            progress.visibility = if ((entry?.resumePosition ?: 0.0) > 0) View.VISIBLE else View.GONE
            progress.progress = if (entry != null && entry.duration > 0) (entry.position / entry.duration * 1000).toInt() else 0
            val uri = row.poster
            val bitmap = uri?.let(artworkCache::get)
            art.setImageBitmap(bitmap)
            if (uri != null && bitmap == null && !posters.isShutdown) posters.submit {
                val decoded = MediaMetadata.loadArtwork(context, uri, 176) ?: return@submit
                artworkCache.put(uri, decoded)
                runOnUiThread { if (!isDestroyed && bound?.poster == uri) art.setImageBitmap(decoded) }
            }
        }
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()

    private companion object {
        const val IMPORT = 1
        const val FOLDER = 2
        const val RELINK = 3
        const val COVER = 4
        const val DIAGNOSTICS = 5
    }
}
