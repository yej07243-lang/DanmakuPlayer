package app.danmaku.player

import android.app.Activity
import android.content.Context
import android.net.Uri
import android.os.Build
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import app.danmaku.player.player.DanmakuOverlay
import app.danmaku.player.player.DanmakuStyle
import java.io.IOException
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.Future
import kotlin.math.abs
import kotlin.math.roundToInt

class DanmakuControls(
    private val activity: Activity,
    private val host: FrameLayout,
    private val danmakuOverlay: DanmakuOverlay,
    private val current: () -> MediaEntry?,
    private val update: (MediaEntry) -> Unit,
    private val playbackPosition: () -> Double,
    private val pickXml: () -> Unit,
    private val showStatus: (String) -> Unit,
    private val onEnabledChanged: (Boolean) -> Unit,
    private val onPanelChanged: (Boolean) -> Unit,
) {
    private enum class Tab(val label: String) { SOURCE("来源"), DISPLAY("显示"), TIMING("时间校准") }
    private enum class Step { HOME, SEARCH, LINK, RESULTS, PAGES, REVIEW, REPLACE_XML }

    private val preferences = activity.getSharedPreferences("danmaku-style", Context.MODE_PRIVATE)
    private val bili = BiliClient(activity)
    private val worker = Executors.newSingleThreadExecutor()
    private var job: Future<*>? = null
    private var generation = 0
    private var sourceRequest = false
    private var message = ""
    private var commentCount = 0
    private var cacheAvailable = false

    private var tab = Tab.SOURCE
    private var step = Step.HOME
    private var official = true
    private var query = ""
    private var link = ""
    private var candidates = emptyList<BiliCandidate>()
    private var candidate: BiliCandidate? = null
    private var pages = emptyList<BiliSource>()
    private var page: BiliSource? = null
    private var pagesBack = Step.RESULTS
    private var wide = false
    private var confirm: Button? = null
    private var pageScroll: ScrollView? = null
    private var pageScrollY = 0

    private val tabs = Tab.entries.map { item ->
        activity.uiButton(item.label) {
            hideKeyboard()
            tab = item
            render()
        }
    }
    private val panelStatus = TextView(activity).apply {
        setTextColor(activity.themeColor(android.R.attr.textColorSecondary))
        maxLines = 3
        setPadding(activity.dp(16), 0, activity.dp(16), activity.dp(4))
        accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
    }
    private val body = FrameLayout(activity)
    private val panel = LinearLayout(activity).apply {
        orientation = LinearLayout.VERTICAL
        isClickable = true
        isFocusableInTouchMode = true
        elevation = activity.dp(16).toFloat()
        if (Build.VERSION.SDK_INT >= 28) accessibilityPaneTitle = "弹幕设置"
        addView(LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(activity.dp(16), 0, activity.dp(8), 0)
            addView(activity.heading("弹幕设置"), LinearLayout.LayoutParams(0, -2, 1f))
            addView(activity.uiButton("关闭") { dismiss() })
        })
        addView(activity.buttonStrip(*tabs.toTypedArray()))
        addView(panelStatus)
        addView(body, LinearLayout.LayoutParams(-1, 0, 1f))
    }

    init {
        host.addView(View(activity).apply {
            setBackgroundColor(0x30000000)
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            setOnClickListener { dismiss() }
            isFocusable = false
        }, FrameLayout.LayoutParams(-1, -1))
        host.addView(panel, FrameLayout.LayoutParams(-1, -1, Gravity.BOTTOM))
        host.addOnLayoutChangeListener { _, left, top, right, bottom, _, _, _, _ ->
            val wasWide = wide
            layoutPanel(right - left, bottom - top)
            if (wasWide != wide && tab == Tab.SOURCE && choosingPage()) render()
        }
        danmakuOverlay.style = DanmakuStyle(
            enabled = preferences.getBoolean("enabled", true),
            font = preferences.getFloat("font", 20f).coerceIn(12f, 36f),
            opacity = preferences.getFloat("opacity", 0.9f).coerceIn(0.1f, 1f),
            area = preferences.getFloat("area", 0.65f).coerceIn(0.2f, 1f),
            speed = preferences.getFloat("speed", 1f).coerceIn(0.5f, 2f),
            scrolling = preferences.getBoolean("scrolling", true),
            top = preferences.getBoolean("top", true),
            bottom = preferences.getBoolean("bottom", true),
            maxVisible = preferences.getInt("maxVisible", 60).coerceIn(8, 160),
            blockedWords = preferences.getString("words", "").orEmpty().lines().filter(String::isNotBlank),
        )
    }

    private fun layoutPanel(width: Int, height: Int) {
        if (width <= 0 || height <= 0) return
        wide = width >= activity.dp(720)
        val showBoth = tab == Tab.SOURCE && choosingPage() && pagesBack == Step.RESULTS
        val panelWidth = when {
            !wide -> width
            showBoth -> (width * 0.75f).toInt().coerceIn(activity.dp(540), activity.dp(760))
            else -> activity.dp(400)
        }
        val panelHeight = if (wide || height < activity.dp(420)) height else (height * 0.78f).toInt()
        val gravity = if (wide) Gravity.END or Gravity.TOP else Gravity.BOTTOM
        val params = panel.layoutParams as FrameLayout.LayoutParams
        if (params.width != panelWidth || params.height != panelHeight || params.gravity != gravity) {
            panel.layoutParams = FrameLayout.LayoutParams(panelWidth, panelHeight, gravity)
        }
    }

    fun reload(entry: MediaEntry?) {
        dismiss()
        cancel()
        cacheAvailable = false
        setComments(emptyList())
        if (entry == null) return
        when {
            entry.xml != null -> run("正在读取弹幕 XML…", { LocalDanmaku.load(activity, entry.xml) }, done = ::setComments)
            entry.source != null -> loadOnline(entry.source)
        }
    }

    fun show() {
        val entry = current() ?: return
        if (host.visibility == View.VISIBLE) return
        tab = Tab.SOURCE
        step = Step.HOME
        official = true
        query = entry.officialQuery
        link = ""
        candidates = emptyList()
        candidate = null
        pages = emptyList()
        page = null
        pageScrollY = 0
        panel.background = activity.rounded(activity.themeColor(android.R.attr.colorBackgroundFloating), 20)
        host.visibility = View.VISIBLE
        onPanelChanged(true)
        render()
        panel.requestFocus()
    }

    fun back(): Boolean {
        if (host.visibility != View.VISIBLE) return false
        if (tab == Tab.SOURCE && step != Step.HOME) {
            navigateBack()
        } else dismiss()
        return true
    }

    private fun navigateBack() {
        hideKeyboard()
        if (sourceRequest) cancel()
        step = when (step) {
            Step.REVIEW -> Step.PAGES
            Step.PAGES -> pagesBack
            Step.RESULTS -> Step.SEARCH
            else -> Step.HOME
        }
        render()
    }

    private fun dismiss() {
        if (host.visibility != View.VISIBLE) return
        hideKeyboard()
        if (sourceRequest) cancel()
        host.visibility = View.GONE
        onPanelChanged(false)
    }

    private fun hideKeyboard() {
        val focus = panel.findFocus() ?: return
        activity.getSystemService(InputMethodManager::class.java).hideSoftInputFromWindow(focus.windowToken, 0)
        panel.requestFocus()
    }

    private fun render() {
        tabs.forEachIndexed { index, button -> activity.selectButton(button, index == tab.ordinal) }
        confirm = null
        pageScroll?.let { pageScrollY = it.scrollY }
        pageScroll = null
        body.removeAllViews()
        layoutPanel(host.width, host.height)
        when (tab) {
            Tab.SOURCE -> renderSource()
            Tab.DISPLAY -> body.addView(scroll(column().apply { displaySettings() }))
            Tab.TIMING -> body.addView(scroll(column().apply { timingSettings() }))
        }
        updatePanelStatus()
    }

    private fun column() = LinearLayout(activity).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(activity.dp(16), activity.dp(6), activity.dp(16), activity.dp(12))
    }

    private fun scroll(contents: LinearLayout) = ScrollView(activity).apply {
        clipToPadding = false
        addView(contents)
    }

    private fun LinearLayout.note(text: String) = TextView(activity).apply {
        this.text = text
        textSize = 15f
        setPadding(0, activity.dp(6), 0, activity.dp(6))
        this@note.addView(this)
    }

    private fun LinearLayout.action(label: String, click: () -> Unit) =
        activity.uiButton(label, click = click).apply {
            gravity = Gravity.START or Gravity.CENTER_VERTICAL
            this@action.addView(this, LinearLayout.LayoutParams(-1, -2))
        }

    private fun choosingPage() = step == Step.PAGES || step == Step.REVIEW

    private fun renderSource() {
        if (choosingPage()) {
            val chooser = if (step == Step.PAGES) pageChooser() else scroll(column().apply { sourceReview() })
            if (wide && pagesBack == Step.RESULTS) {
                body.addView(LinearLayout(activity).apply {
                    addView(scroll(column().apply { candidateList() }), LinearLayout.LayoutParams(0, -1, 1f))
                    addView(chooser, LinearLayout.LayoutParams(0, -1, 1f))
                }, FrameLayout.LayoutParams(-1, -1))
            } else body.addView(chooser, FrameLayout.LayoutParams(-1, -1))
            return
        }
        body.addView(scroll(column().apply {
            when (step) {
                Step.HOME -> sourceHome()
                Step.SEARCH, Step.LINK -> sourceForm()
                Step.RESULTS -> candidateList()
                Step.REPLACE_XML -> {
                    action("‹ 返回来源", ::navigateBack)
                    note("更换为本地 XML 后会重置整体延后和分段校准。")
                    action("选择本地 XML", pickXml)
                }
                Step.PAGES, Step.REVIEW -> Unit
            }
        }))
    }

    private fun LinearLayout.sourceHome() {
        val entry = current() ?: return
        val source = entry.source
        val label = when {
            entry.xml != null -> "本地 XML"
            source != null -> "B 站 · ${source.title.ifBlank { source.bvid }}"
            else -> "尚未关联弹幕"
        }
        addView(activity.heading(label, 17f))
        action("片名搜索") { goTo(Step.SEARCH) }
        action("粘贴链接") { goTo(Step.LINK) }
        action("导入本地 XML", ::chooseXml)
        if (source != null && entry.xml == null) {
            action("刷新 B 站弹幕") { loadOnline(source, refresh = true) }
            action("清除 B 站缓存") {
                run("正在清除缓存…", { bili.clearCache(source) }) {
                    cacheAvailable = false
                    setComments(emptyList())
                    setStatus("已清除缓存，可刷新重新下载")
                }
            }
        }
        if (source != null || entry.xml != null) {
            action(if (entry.xml == null) "解除弹幕关联" else "清除本地弹幕") {
                val latest = current() ?: return@action
                cancel()
                update(latest.copy(source = null, xml = null, offset = 0.0, points = emptyList()))
                LocalDanmaku.delete(activity, latest.xml)
                cacheAvailable = false
                setComments(emptyList())
                render()
            }
        }
    }

    private fun goTo(next: Step) {
        hideKeyboard()
        if (sourceRequest) cancel()
        step = next
        render()
    }

    private fun needsReset(entry: MediaEntry) =
        entry.source != null || entry.xml != null || entry.offset != 0.0 || entry.points.isNotEmpty()

    private fun chooseXml() {
        val entry = current() ?: return
        if (needsReset(entry)) goTo(Step.REPLACE_XML) else pickXml()
    }

    fun loadXml(uri: Uri) {
        val id = current()?.id ?: return
        run("正在读取弹幕 XML…", { LocalDanmaku.copy(activity, uri) }, selecting = true,
            discard = { LocalDanmaku.delete(activity, it.uri) }) { imported ->
            val entry = current()?.takeIf { it.id == id }
            if (entry == null) {
                LocalDanmaku.delete(activity, imported.uri)
                return@run
            }
            update(entry.copy(source = null, xml = imported.uri, offset = 0.0, points = emptyList()))
            LocalDanmaku.delete(activity, entry.xml)
            cacheAvailable = false
            Diagnostics.record(activity, "DanmakuXml", "Imported ${imported.comments.size} comments")
            setComments(imported.comments)
            step = Step.HOME
            if (host.visibility == View.VISIBLE && tab == Tab.SOURCE) render()
        }
    }

    private fun LinearLayout.sourceForm() {
        val isLink = step == Step.LINK
        action("‹ 返回来源", ::navigateBack)
        val label = if (isLink) "B 站视频链接或 BV" else "片名"
        val input = field(label, if (isLink) link else query,
            InputType.TYPE_CLASS_TEXT or if (isLink) InputType.TYPE_TEXT_VARIATION_URI else 0)
        input.imeOptions = EditorInfo.IME_ACTION_SEARCH or EditorInfo.IME_FLAG_NO_EXTRACT_UI
        input.watch { if (isLink) link = it else query = it }
        if (!isLink) {
            val modes = listOf(true to "官方影视", false to "普通视频").map { (value, title) ->
                activity.uiButton(title) {
                    if (official == value) return@uiButton
                    hideKeyboard()
                    if (sourceRequest) cancel()
                    val entry = current()
                    val previousDefault = entry?.let { if (official) it.officialQuery else it.searchQuery }
                    if (entry != null && query == previousDefault) {
                        query = if (value) entry.officialQuery else entry.searchQuery
                    }
                    official = value
                    render()
                }.apply { activity.selectButton(this, value == official) }
            }
            addView(activity.buttonStrip(*modes.toTypedArray()))
        }
        fun submit() {
            val submitted = input.text.toString().trim()
            if (submitted.isBlank()) {
                input.error = if (isLink) "请输入链接或 BV" else "请输入片名"
                return
            }
            hideKeyboard()
            if (isLink) {
                run("正在解析链接…", { bili.resolve(submitted) }, selecting = true) {
                    showPages(it.pages, it.selected, Step.LINK)
                }
            } else {
                val officialSearch = official
                run("正在搜索 B 站…", { bili.search(submitted, officialSearch) }, selecting = true) {
                    if (it.isEmpty()) setStatus("没有搜索结果，可以更换片名、粘贴链接或导入 XML")
                    else {
                        candidates = it
                        candidate = null
                        pages = emptyList()
                        page = null
                        step = Step.RESULTS
                        render()
                    }
                }
            }
        }
        action(if (isLink) "解析链接" else "搜索 B 站", ::submit)
        input.setOnEditorActionListener { _, id, _ ->
            if (id != EditorInfo.IME_ACTION_SEARCH) false else { submit(); true }
        }
        fallback(showLink = !isLink)
    }

    private fun LinearLayout.fallback(showLink: Boolean = true) {
        if (showLink) action("粘贴链接") { goTo(Step.LINK) }
        action("导入本地 XML", ::chooseXml)
    }

    private fun LinearLayout.candidateList() {
        if (wide && choosingPage()) action("‹ 返回搜索") { goTo(Step.SEARCH) }
        else action("‹ 返回搜索", ::navigateBack)
        addView(activity.heading("选择候选", 17f))
        candidates.forEach { item ->
            val label = listOf(item.title, item.detail).filter(String::isNotBlank).joinToString("\n")
            val button = action(label) {
                if (item == candidate && pages.isNotEmpty() && pagesBack == Step.RESULTS) {
                    if (sourceRequest) cancel()
                    step = Step.PAGES
                    render()
                } else run("正在读取集数和分 P…", { bili.pages(item) }, selecting = true) { results ->
                    if (results.isEmpty()) setStatus("没有可用的集数或分 P")
                    else {
                        candidate = item
                        showPages(results, null, Step.RESULTS)
                    }
                }
            }
            activity.selectButton(button, item == candidate)
        }
        fallback()
    }

    private fun showPages(results: List<BiliSource>, selected: BiliSource?, back: Step) {
        if (results.isEmpty()) {
            setStatus("没有可用的集数或分 P")
            return
        }
        pagesBack = back
        pages = results
        page = selected ?: results.first()
        pageScroll = null
        pageScrollY = 0
        step = Step.PAGES
        render()
    }

    private fun pageChooser() = LinearLayout(activity).apply {
        orientation = LinearLayout.VERTICAL
        val list = scroll(column().apply {
            action(if (pagesBack == Step.LINK) "‹ 返回链接" else "‹ 返回候选", ::navigateBack)
            addView(activity.heading("选择单集或分 P", 17f))
            val group = android.widget.RadioGroup(activity)
            pages.forEach { item ->
                group.addView(android.widget.RadioButton(activity).apply {
                    id = View.generateViewId()
                    text = pageLabel(item)
                    minHeight = activity.dp(48)
                    isChecked = item == page
                    setOnClickListener { page = item }
                })
            }
            addView(group)
            fallback(showLink = pagesBack != Step.LINK)
        })
        pageScroll = list
        val position = pageScrollY
        list.post { list.scrollTo(0, position) }
        addView(list, LinearLayout.LayoutParams(-1, 0, 1f))
        addView(activity.uiButton("检查所选来源") { goTo(Step.REVIEW) })
    }

    private fun LinearLayout.sourceReview() {
        val source = page ?: return
        val entry = current() ?: return
        action("‹ 返回单集或分 P", ::navigateBack)
        addView(activity.heading("确认弹幕来源", 17f))
        note("B 站 · ${pageLabel(source)}\n${source.bvid}")
        if (!sameSource(entry, source) && needsReset(entry)) {
            note("成功更换后会重置整体延后和分段校准。")
        }
        confirm = action("关联并下载", ::confirmSource)
        fallback(showLink = pagesBack != Step.LINK)
    }

    private fun pageLabel(source: BiliSource): String {
        val title = source.title.ifBlank { "未命名" }
        val label = if (title.startsWith("第")) title else "P${source.page} · $title"
        return if (source.duration > 0) "$label · ${playbackTime(source.duration)}" else label
    }

    private fun sameSource(entry: MediaEntry, source: BiliSource): Boolean =
        entry.xml == null && entry.source?.cid == source.cid && entry.source.bvid == source.bvid

    private fun confirmSource() {
        val id = current()?.id ?: return
        val source = page ?: return
        run("正在关联并加载 B 站弹幕…", { bili.comments(source) }, selecting = true) { comments ->
            val entry = current()?.takeIf { it.id == id } ?: return@run
            val same = sameSource(entry, source)
            update(entry.copy(source = source, xml = null, offset = if (same) entry.offset else 0.0,
                points = if (same) entry.points else emptyList()))
            LocalDanmaku.delete(activity, entry.xml)
            cacheAvailable = true
            setComments(comments)
            step = Step.HOME
            if (host.visibility == View.VISIBLE && tab == Tab.SOURCE) render()
        }
    }

    private fun loadOnline(source: BiliSource, refresh: Boolean = false) {
        run("正在加载 B 站弹幕…", {
            try {
                bili.comments(source, refresh) to false
            } catch (error: Exception) {
                if (!refresh || Thread.currentThread().isInterrupted) throw error
                val cached = bili.cachedComments(source) ?: throw error
                cached to true
            }
        }) { (comments, fallback) ->
            cacheAvailable = true
            setComments(comments)
            if (fallback) setStatus("刷新失败，继续使用已缓存的弹幕")
        }
    }

    private fun LinearLayout.displaySettings() {
        fun setting(label: String, value: Int, range: IntRange, format: (Int) -> String, change: (Int) -> Unit) {
            val valueLabel = note("$label · ${format(value)}")
            addView(SeekBar(activity).apply {
                contentDescription = label
                minHeight = activity.dp(48)
                max = range.last - range.first
                progress = value.coerceIn(range) - range.first
                if (Build.VERSION.SDK_INT >= 30) stateDescription = format(value)
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(bar: SeekBar, progress: Int, fromUser: Boolean) {
                        if (!fromUser) return
                        val next = range.first + progress
                        valueLabel.text = "$label · ${format(next)}"
                        if (Build.VERSION.SDK_INT >= 30) bar.stateDescription = format(next)
                        change(next)
                    }
                    override fun onStartTrackingTouch(bar: SeekBar) = Unit
                    override fun onStopTrackingTouch(bar: SeekBar) = Unit
                })
            })
        }
        fun check(label: String, value: Boolean, change: (Boolean) -> Unit) {
            addView(CheckBox(activity).apply {
                text = label
                isChecked = value
                minHeight = activity.dp(48)
                setOnCheckedChangeListener { _, checked -> change(checked) }
            })
        }
        val style = danmakuOverlay.style
        check("显示弹幕", style.enabled) { saveStyle(danmakuOverlay.style.copy(enabled = it)) }
        setting("字号", style.font.roundToInt(), 12..36, { "$it sp" }) { saveStyle(danmakuOverlay.style.copy(font = it.toFloat())) }
        setting("不透明度", (style.opacity * 100).roundToInt(), 10..100, { "$it%" }) {
            saveStyle(danmakuOverlay.style.copy(opacity = it / 100f))
        }
        setting("滚动区域", (style.area * 100).roundToInt(), 20..100, { "$it%" }) {
            saveStyle(danmakuOverlay.style.copy(area = it / 100f))
        }
        setting("滚动速度", (style.speed * 10).roundToInt(), 5..20, { "${it / 10f}×" }) {
            saveStyle(danmakuOverlay.style.copy(speed = it / 10f))
        }
        setting("同屏上限", style.maxVisible, 8..160, { "$it 条" }) {
            saveStyle(danmakuOverlay.style.copy(maxVisible = it))
        }
        check("滚动弹幕", style.scrolling) { saveStyle(danmakuOverlay.style.copy(scrolling = it)) }
        check("顶部弹幕", style.top) { saveStyle(danmakuOverlay.style.copy(top = it)) }
        check("底部弹幕", style.bottom) { saveStyle(danmakuOverlay.style.copy(bottom = it)) }
        field("屏蔽词（每行一个）", style.blockedWords.joinToString("\n"),
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE, multiline = true).watch { words ->
            saveStyle(danmakuOverlay.style.copy(blockedWords = words.lines().map(String::trim).filter(String::isNotBlank)))
        }
        action("恢复默认显示设置") {
            hideKeyboard()
            saveStyle(DanmakuStyle())
            render()
        }
    }

    private fun LinearLayout.timingSettings() {
        val entry = current() ?: return
        note("正数让弹幕延后，负数让弹幕提前。")
        val number = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL
        val offset = field("整体延后（秒）", seconds(entry.offset), number or InputType.TYPE_NUMBER_FLAG_SIGNED)
        fun applyOffset(value: Double) {
            val latest = current() ?: return
            update(latest.copy(offset = value))
            offset.setText(seconds(value))
        }
        addView(activity.buttonStrip(
            activity.uiButton("提前 0.5 秒") { current()?.let { applyOffset((it.offset - 0.5).coerceAtLeast(-86_400.0)) } },
            activity.uiButton("延后 0.5 秒") { current()?.let { applyOffset((it.offset + 0.5).coerceAtMost(86_400.0)) } },
        ))
        fun saveOffset() {
            readSeconds(offset, -86_400.0..86_400.0)?.let {
                hideKeyboard()
                applyOffset(it)
            }
        }
        action("保存整体延后", ::saveOffset)
        offset.onImeDone(::saveOffset)
        addView(activity.heading("分段校准点", 17f))
        val anchor = note("播放到要校准的位置，再取当前时间。")
        val sourceTime = field("来源时间（秒）", "", number)
        var localTime: Double? = null
        action("取当前播放位置") {
            val local = playbackPosition().takeIf { it.isFinite() && it >= 0 } ?: return@action
            val latest = current() ?: return@action
            localTime = local
            anchor.text = "本地时间 · ${playbackTime(local)}（${seconds(local)} 秒）"
            sourceTime.setText(seconds((latest.sourceTime(local) + latest.offset).coerceAtLeast(0.0)))
        }
        val points = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        fun addPoint() {
            val local = localTime
            if (local == null) {
                sourceTime.error = "请先取当前播放位置"
                return
            }
            val source = readSeconds(sourceTime, 0.0..86_400.0) ?: return
            val latest = current() ?: return
            val updated = latest.points.filterNot { abs(it.local - local) < 0.1 } + CalibrationPoint(local, source)
            update(latest.copy(points = updated.sortedBy(CalibrationPoint::local)))
            hideKeyboard()
            points.renderPoints()
        }
        action("添加校准点", ::addPoint)
        sourceTime.onImeDone(::addPoint)
        addView(points)
        points.renderPoints()
    }

    private fun LinearLayout.renderPoints() {
        removeAllViews()
        val points = current()?.points.orEmpty()
        if (points.isEmpty()) note("还没有校准点")
        points.forEach { point ->
            val label = "本地 ${seconds(point.local)} 秒 → 来源 ${seconds(point.source)} 秒"
            addView(LinearLayout(activity).apply {
                gravity = Gravity.CENTER_VERTICAL
                addView(TextView(activity).apply { text = label }, LinearLayout.LayoutParams(0, -2, 1f))
                addView(activity.uiButton("删除", "删除校准点，$label") {
                    current()?.let { update(it.copy(points = it.points - point)) }
                    this@renderPoints.renderPoints()
                })
            })
        }
    }

    private fun LinearLayout.field(label: String, value: String, type: Int, multiline: Boolean = false): EditText {
        val input = EditText(activity).apply {
            id = View.generateViewId()
            if (!multiline) setSingleLine() else minLines = 2
            inputType = type
            minHeight = activity.dp(48)
            setText(value)
            imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI
        }
        note(label).labelFor = input.id
        addView(input, LinearLayout.LayoutParams(-1, -2))
        return input
    }

    private fun EditText.onImeDone(save: () -> Unit) {
        imeOptions = EditorInfo.IME_ACTION_DONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI
        setOnEditorActionListener { _, action, _ ->
            if (action != EditorInfo.IME_ACTION_DONE) return@setOnEditorActionListener false
            save()
            true
        }
    }

    private fun EditText.watch(change: (String) -> Unit) {
        addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = change(s.toString())
            override fun afterTextChanged(s: Editable?) = Unit
        })
    }

    private fun readSeconds(input: EditText, range: ClosedFloatingPointRange<Double>): Double? {
        val value = input.text.toString().trim().replace(',', '.').toDoubleOrNull()?.takeIf { it in range }
        if (value == null) input.error = "请输入 ${range.start} 到 ${range.endInclusive} 秒之间的有效数字"
        return value
    }

    private fun seconds(value: Double) = String.format(Locale.ROOT, "%.2f", value)

    fun toggle() = saveStyle(danmakuOverlay.style.copy(enabled = !danmakuOverlay.style.enabled))

    private fun saveStyle(style: DanmakuStyle) {
        danmakuOverlay.style = style
        preferences.edit().putBoolean("enabled", style.enabled).putFloat("font", style.font)
            .putFloat("opacity", style.opacity).putFloat("area", style.area).putFloat("speed", style.speed)
            .putInt("maxVisible", style.maxVisible)
            .putBoolean("scrolling", style.scrolling).putBoolean("top", style.top).putBoolean("bottom", style.bottom)
            .putString("words", style.blockedWords.joinToString("\n")).apply()
        onEnabledChanged(style.enabled)
    }

    private fun setComments(comments: List<Danmaku>) {
        commentCount = comments.size
        danmakuOverlay.setComments(comments)
        setStatus("")
    }

    private fun setStatus(status: String) {
        message = status
        showStatus(status.ifBlank { if (commentCount == 0) "" else "$commentCount 条弹幕" })
        updatePanelStatus()
    }

    private fun updatePanelStatus() {
        val cache = if (cacheAvailable) "已缓存 · $commentCount 条弹幕" else if (commentCount > 0) "$commentCount 条弹幕" else ""
        val text = listOf(message, cache).filter(String::isNotBlank).joinToString("\n")
        if (panelStatus.text != text) panelStatus.text = text
        panelStatus.visibility = if (text.isBlank()) View.GONE else View.VISIBLE
        confirm?.isEnabled = !sourceRequest
    }

    private fun <T> run(
        status: String,
        work: () -> T,
        selecting: Boolean = false,
        discard: (T) -> Unit = {},
        done: (T) -> Unit,
    ) {
        cancel()
        sourceRequest = selecting
        val token = generation
        setStatus(status)
        job = worker.submit {
            val result = runCatching(work)
            activity.runOnUiThread {
                if (activity.isFinishing || activity.isDestroyed || generation != token) {
                    result.getOrNull()?.let(discard)
                    return@runOnUiThread
                }
                sourceRequest = false
                job = null
                result.onSuccess {
                    setStatus("")
                    done(it)
                }.onFailure {
                    val message = if (it is IOException) it.localizedMessage ?: "读取失败" else "返回的数据无法解析"
                    Diagnostics.record(activity, "DanmakuSource", "Source operation failed: ${it.javaClass.simpleName}")
                    setStatus(message)
                    if (host.visibility != View.VISIBLE) Toast.makeText(activity, message, Toast.LENGTH_SHORT).show()
                }
            }
        }
    }

    private fun cancel() {
        generation++
        job?.cancel(true)
        job = null
        sourceRequest = false
        setStatus("")
    }

    fun close() {
        cancel()
        worker.shutdownNow()
    }
}
