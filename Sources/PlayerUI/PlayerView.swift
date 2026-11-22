import SwiftUI
import AppKit
import CoreText
import QuartzCore
import CoreText
import MPVBridge
import PlayerCore

struct MPVSurface: NSViewRepresentable {
    let video: DPVideoView
    func makeNSView(context: Context) -> DPVideoView { video }
    func updateNSView(_ view: DPVideoView, context: Context) {}
}
struct PlayerView: View {
    @EnvironmentObject var app: AppModel
    @ObservedObject var session: PlayerSession
    @State private var dragPosition: Double?
    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Color.black
                MPVSurface(video: session.video)
                DanmakuOverlay(session: session, settings: app.settings).allowsHitTesting(false)
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
            VStack(spacing: 10) {
                if !session.error.isEmpty {
                    HStack(alignment: .top) {
                        Text(session.error).font(.callout).foregroundStyle(.red).textSelection(.enabled)
                        Spacer(); Button { session.error = "" } label: { Image(systemName: "xmark") }.buttonStyle(.plain).accessibilityLabel("关闭错误提示")
                    }
                }
                HStack {
                    Text(formatTime(dragPosition ?? session.position)).monospacedDigit().frame(minWidth: 50, alignment: .leading)
                    Slider(value: Binding(get: { dragPosition ?? min(session.position, max(1, session.duration)) }, set: { dragPosition = $0 }), in: 0...max(1, session.duration), onEditingChanged: { editing in if !editing, let time = dragPosition { session.seek(time); dragPosition = nil } }).accessibilityLabel("播放进度")
                    Text(formatTime(session.duration)).monospacedDigit().frame(minWidth: 50, alignment: .trailing)
                }
                HStack(spacing: 18) {
                    Button { session.seek(session.position - 5) } label: { Image(systemName: "gobackward.5") }.accessibilityLabel("后退五秒").keyboardShortcut(.leftArrow, modifiers: [])
                    Button { session.setPaused(!session.paused) } label: { Image(systemName: session.paused ? "play.fill" : "pause.fill").frame(width: 24) }.accessibilityLabel(session.paused ? "播放" : "暂停").keyboardShortcut(.space, modifiers: [])
                    Button { session.seek(session.position + 5) } label: { Image(systemName: "goforward.5") }.accessibilityLabel("前进五秒").keyboardShortcut(.rightArrow, modifiers: [])
                    Menu {
                        ForEach([0.5, 0.75, 1, 1.25, 1.5, 2], id: \.self) { speed in Button("\(speed.formatted())×") { session.setRate(speed) } }
                    } label: { Text("\(session.rate.formatted())×") }.menuStyle(.borderlessButton).frame(width: 55)
                    Menu {
                        Section("音轨") { ForEach(session.tracks.filter { $0.type == "audio" }) { track in Button((track.selected ? "✓ " : "") + track.label) { session.selectTrack(track) } } }
                        Section("字幕") {
                            Button("关闭字幕") { session.video.setString("no", property: "sid"); session.refreshTracks() }
                            ForEach(session.tracks.filter { $0.type == "sub" }) { track in Button((track.selected ? "✓ " : "") + track.label) { session.selectTrack(track) } }
                            Button("加载字幕文件…") { session.addSubtitle() }
                            Button("字幕提前 0.1 秒") { session.video.setNumber(session.video.number("sub-delay") - 0.1, property: "sub-delay") }
                            Button("字幕延后 0.1 秒") { session.video.setNumber(session.video.number("sub-delay") + 0.1, property: "sub-delay") }
                        }
                    } label: { Label("字幕与音轨", systemImage: "captions.bubble").labelStyle(.iconOnly) }.accessibilityLabel("字幕与音轨").menuStyle(.borderlessButton).frame(width: 28)
                    Menu {
                        ForEach(app.filtered) { item in Button(item.title + (item.episode.map { " E\($0)" } ?? "")) { app.play(item) } }
                    } label: { Label("播放列表", systemImage: "list.bullet").labelStyle(.iconOnly) }.accessibilityLabel("播放列表").menuStyle(.borderlessButton).frame(width: 28)
                    Spacer(minLength: 4)
                    if session.loading { ProgressView().controlSize(.small) }
                    Toggle(isOn: $app.settings.enabled) { Image(systemName: "text.bubble") }.toggleStyle(.button).accessibilityLabel("弹幕开关").keyboardShortcut("d", modifiers: [])
                    Button { session.showSettings.toggle() } label: { Image(systemName: "slider.horizontal.3") }.accessibilityLabel("弹幕设置").popover(isPresented: $session.showSettings) { DanmakuControls(session: session).environmentObject(app).frame(width: 380).padding(20).background(Color(nsColor: .windowBackgroundColor)) }
                    Image(systemName: "speaker.wave.2").accessibilityHidden(true)
                    Slider(value: Binding(get: { session.volume }, set: session.setVolume), in: 0...100).frame(width: 80).accessibilityLabel("音量")
                    Button { session.video.window?.toggleFullScreen(nil) } label: { Image(systemName: "arrow.up.left.and.arrow.down.right") }.accessibilityLabel("全屏").keyboardShortcut("f", modifiers: [])
                }.buttonStyle(.borderless)
            }.padding(16).background(.bar)
        }.preferredColorScheme(.dark)
    }
}
struct DanmakuControls: View {
    @EnvironmentObject var app: AppModel
    @ObservedObject var session: PlayerSession
    @State private var sourceSeconds = 0.0
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack { Text("弹幕").font(.title2.bold()); Spacer(); Text(session.status).font(.caption).foregroundStyle(.secondary) }
            Form {
                HStack {
                    Toggle("滚动", isOn: $app.settings.scrolling)
                    Toggle("顶部", isOn: $app.settings.top)
                    Toggle("底部", isOn: $app.settings.bottom)
                }.toggleStyle(.checkbox)
                HStack {
                    Slider(value: $app.settings.scrollSpeed, in: 0.5...2, step: 0.05) { Text("滚动速度") }
                    Text(app.settings.scrollSpeed, format: .number.precision(.fractionLength(2))).monospacedDigit().frame(width: 40)
                }
                Slider(value: $app.settings.fontSize, in: 16...40) { Text("字号") }
                Slider(value: $app.settings.opacity, in: 0.2...1) { Text("透明度") }
                Slider(value: $app.settings.area, in: 0.25...1) { Text("显示区域") }
                Stepper("同屏上限 \(app.settings.maxVisible)", value: $app.settings.maxVisible, in: 8...160, step: 8)
                TextField("屏蔽词，每行一个", text: $app.settings.blockedWords, axis: .vertical).lineLimit(2...4)
            }
            Button("恢复显示默认值") { app.settings = DanmakuSettings() }
            Divider()
            HStack {
                Text("整体延后")
                TextField("秒", value: $session.item.calibration.offset, format: .number).frame(width: 80)
                Text("秒")
                Button("−0.5") { session.item.calibration.offset -= 0.5; session.persist() }
                Button("+0.5") { session.item.calibration.offset += 0.5; session.persist() }
            }
            HStack {
                Text("此处对应 B 站")
                TextField("秒", value: $sourceSeconds, format: .number).frame(width: 70)
                Text("秒")
                Button("添加校准点") {
                    guard sourceSeconds.isFinite, sourceSeconds >= 0 else { return }
                    let local = session.position
                    session.item.calibration.points.removeAll { abs($0.local - local) < 0.1 }
                    session.item.calibration.points.append(.init(local: local, source: sourceSeconds)); session.persist()
                }
            }
            if !session.item.calibration.points.isEmpty {
                ScrollView {
                    ForEach(session.item.calibration.points.sorted { $0.local < $1.local }) { point in
                        HStack { Text("\(formatTime(point.local)) → \(formatTime(point.source))").monospacedDigit(); Spacer(); Button("删除") { session.item.calibration.points.removeAll { $0.id == point.id }; session.persist() } }
                    }
                }.frame(maxHeight: 100)
            }
            Divider()
            HStack {
                Button("搜索来源") { app.showSource(session.item) }
                Button("B 站链接") { app.showSource(session.item, link: true) }
                Button("导入 XML") { session.importXML() }
                Menu("缓存") {
                    Button("刷新 B 站弹幕") { session.loadComments(refresh: true) }.disabled(session.item.source == nil || session.loading)
                    Button("清除缓存") { session.clearCache() }
                }.frame(width: 70)
            }
        }.onAppear { sourceSeconds = max(0, session.item.calibration.sourceTime(local: session.position) + session.item.calibration.offset) }
        .onChange(of: session.item.calibration) { _, _ in session.persist() }
    }
}
private struct PositionedComment {
    let comment: Danmaku
    let row: Int
    let width: Double
    let lifetime: Double
    let line: CTLine
    let outline: CTLine
    let baselineOffset: Double
}
struct DanmakuOverlay: NSViewRepresentable {
    @ObservedObject var session: PlayerSession
    let settings: DanmakuSettings
    func makeNSView(context: Context) -> DanmakuDrawingView { DanmakuDrawingView(session: session, settings: settings) }
    func updateNSView(_ view: DanmakuDrawingView, context: Context) { view.update(session: session, settings: settings) }
    static func dismantleNSView(_ view: DanmakuDrawingView, coordinator: ()) { view.stop() }
}

/// AppKit's view display link keeps animation independent of SwiftUI's update scheduling.
final class DanmakuDrawingView: NSView {
    private weak var session: PlayerSession?
    private var settings: DanmakuSettings
    private var comments: [Danmaku] = []
    private var layout: [PositionedComment] = []
    private var displayClock: CADisplayLink?
    override var isOpaque: Bool { false }
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    init(session: PlayerSession, settings: DanmakuSettings) {
        self.session = session; self.settings = settings
        super.init(frame: .zero)
        wantsLayer = true; layer?.isOpaque = false
        layerContentsRedrawPolicy = .onSetNeedsDisplay
        update(session: session, settings: settings)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }
    func stop() { displayClock?.invalidate(); displayClock = nil }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow(); stop()
        guard window != nil else { return }
        let link = displayLink(target: self, selector: #selector(step(_:)))
        link.preferredFrameRateRange = CAFrameRateRange(minimum: 60, maximum: 60, preferred: 60)
        link.add(to: .main, forMode: .common); displayClock = link
        updateAnimation()
    }
    private func updateAnimation() {
        displayClock?.isPaused = session?.paused != false || !settings.enabled || comments.isEmpty
    }
    func update(session: PlayerSession, settings: DanmakuSettings) {
        self.session = session
        if self.settings != settings || comments != session.comments {
            self.settings = settings; comments = session.comments; rebuild(size: bounds.size)
        }
        updateAnimation(); needsDisplay = true
    }
    override func setFrameSize(_ newSize: NSSize) {
        let changed = frame.size != newSize
        super.setFrameSize(newSize)
        if changed { rebuild(size: newSize); needsDisplay = true }
    }
    @objc private func step(_ sender: CADisplayLink) { needsDisplay = true; displayIfNeeded() }
    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.clear(bounds)
        guard settings.enabled, let session else { return }
        if !session.paused && !comments.isEmpty { RenderCadenceProbe.shared.record() }
        let time = session.item.calibration.sourceTime(local: session.danmakuClock.time(at: ProcessInfo.processInfo.systemUptime))
        var low = 0, high = layout.count
        while low < high { let mid = (low + high) / 2; if layout[mid].comment.time < time - max(4, settings.scrollingDuration) { low = mid + 1 } else { high = mid } }
        for placed in layout[low...] {
            let entry = placed.comment
            if entry.time > time { break }
            let elapsed = time - entry.time
            guard elapsed >= 0 && elapsed < placed.lifetime else { continue }
            let x = entry.mode == 4 || entry.mode == 5 ? bounds.width / 2 : bounds.width + placed.width / 2 - (bounds.width + placed.width) * elapsed / placed.lifetime
            let y = Double(placed.row) * (settings.fontSize + 8) + settings.fontSize / 2 + 8
            context.saveGState(); context.setAlpha(settings.opacity)
            context.translateBy(x: x - placed.width / 2, y: y + placed.baselineOffset)
            context.scaleBy(x: 1, y: -1)
            context.textMatrix = .identity; context.textPosition = .zero
            CTLineDraw(placed.outline, context); context.textPosition = .zero; CTLineDraw(placed.line, context)
            context.restoreGState()
        }
    }
    private func rebuild(size: CGSize) {
        let count = max(3, Int(size.height * settings.area / (settings.fontSize + 8)))
        var previous = Array<(time: Double, width: Double)?>(repeating: nil, count: count)
        var output: [PositionedComment] = []
        let words = settings.blockedWords.components(separatedBy: .newlines).filter { !$0.isEmpty }
        var bucket = -1, accepted = 0
        for entry in comments {
            guard settings.allows(mode: entry.mode) else { continue }
            if words.contains(where: { entry.text.localizedCaseInsensitiveContains($0) }) { continue }
            let second = Int(entry.time)
            if second != bucket { bucket = second; accepted = 0 }
            guard accepted < max(1, settings.maxVisible / Int(ceil(max(4, settings.scrollingDuration)))) else { continue }
            let fixed = entry.mode == 4 || entry.mode == 5
            let life = fixed ? 4.0 : settings.scrollingDuration
            let color = NSColor(srgbRed: Double(entry.color >> 16 & 255) / 255, green: Double(entry.color >> 8 & 255) / 255, blue: Double(entry.color & 255) / 255, alpha: 1)
            let font = NSFont.systemFont(ofSize: settings.fontSize, weight: .semibold)
            let line = CTLineCreateWithAttributedString(NSAttributedString(string: entry.text, attributes: [.font: font, .foregroundColor: color]))
            let outline = CTLineCreateWithAttributedString(NSAttributedString(string: entry.text, attributes: [.font: font, .foregroundColor: NSColor.black, .strokeColor: NSColor.black, .strokeWidth: 8]))
            var ascent: CGFloat = 0, descent: CGFloat = 0
            let width = CTLineGetTypographicBounds(line, &ascent, &descent, nil)
            let lanes: [Int] = entry.mode == 5 ? [0] : entry.mode == 4 ? [count - 1] : Array(1..<max(2, count - 1))
            if let row = lanes.first(where: { row in
                guard let last = previous[row] else { return true }
                let delta = entry.time - last.time
                if fixed { return delta >= life }
                let previousSpeed = (size.width + last.width) / life
                let speed = (size.width + width) / life
                return delta >= (last.width + 24) / previousSpeed && delta >= life - (size.width - 24) / speed
            }) {
                previous[row] = (entry.time, width); accepted += 1
                output.append(PositionedComment(comment: entry, row: row, width: width, lifetime: life, line: line, outline: outline, baselineOffset: (ascent - descent) / 2))
            }
        }
        layout = output
    }
}
