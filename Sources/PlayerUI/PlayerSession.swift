import AppKit
import SwiftUI
import Combine
import MPVBridge
import PlayerCore

struct MediaTrack: Identifiable {
    let id: Int
    let type: String
    let label: String
    let selected: Bool
}
@MainActor final class PlayerSession: ObservableObject {
    @Published var item: MediaItem
    @Published var position = 0.0
    @Published var duration = 0.0
    @Published var paused = false
    @Published var rate = 1.0
    @Published var volume = 100.0
    @Published var tracks: [MediaTrack] = []
    @Published var comments: [Danmaku] = []
    @Published var status = ""
    @Published var error = ""
    @Published var loading = false
    @Published var showSettings = false
    let video: DPVideoView
    unowned let app: AppModel
    private var timer: Timer?
    private var lastSave = Date.distantPast
    private var desiredResume: Double?
    private var commentTask: Task<Void, Never>?
    private var observers: [NSObjectProtocol] = []
    private var closed = false
    private var tickCount = 0
    private var lastEngineError = ""
    private var sourceGeneration = 0
    private var playbackActivity: NSObjectProtocol?
    var preventsIdleSleep: Bool { playbackActivity != nil }
    var sampleDate = Date()
    var danmakuClock = PlaybackClock()
    private var clockDiscontinuity = false
    private func sampleOverlayClock(discontinuity: Bool = false) { danmakuClock.sample(position: position, at: ProcessInfo.processInfo.systemUptime, rate: rate, paused: paused, discontinuity: discontinuity) }
    init(item: MediaItem, app: AppModel) {
        self.item = item; self.app = app
        video = DPVideoView(frame: NSRect(x: 0, y: 0, width: 960, height: 540))
        if !video.engineError.isEmpty { error = video.engineError }
        desiredResume = item.watched ? nil : item.position
        video.command(["loadfile", item.path])
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in MainActor.assumeIsolated { self?.tick() } }
        if let timer { RunLoop.main.add(timer, forMode: .common) }
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.setPaused(true); self?.persist() } })
        loadComments()
        Diagnostics.log("player started engine=\(video.version)")
    }
    func tick() {
        guard !closed else { return }
        sampleDate = Date(); video.poll(); position = video.number("time-pos"); duration = video.number("duration"); paused = video.string("pause") == "yes"
        updatePlaybackActivity()
        let engineError = video.engineError
        if engineError != lastEngineError {
            lastEngineError = engineError
            if !engineError.isEmpty { error = engineError }
        }
        if let resume = desiredResume, duration > 0 {
            desiredResume = nil
            if resume > 0 && resume < duration - 2 { seek(resume) }
        }
        sampleOverlayClock(discontinuity: clockDiscontinuity); clockDiscontinuity = false
        tickCount += 1
        if tickCount % 10 == 0 { refreshTracks() }
        if Date().timeIntervalSince(lastSave) >= 5 { persist() }
    }
    func refreshTracks() {
        guard let data = video.json("track-list").data(using: .utf8), let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return }
        tracks = list.compactMap { row in
            guard let id = row["id"] as? Int, let type = row["type"] as? String else { return nil }
            let values = [row["title"] as? String, row["lang"] as? String, row["codec"] as? String].compactMap { $0 }.filter { !$0.isEmpty }
            return MediaTrack(id: id, type: type, label: values.isEmpty ? "轨道 \(id)" : values.joined(separator: " · "), selected: row["selected"] as? Bool ?? false)
        }
    }
    func seek(_ time: Double) { clockDiscontinuity = true; video.command(["seek", String(min(max(0, time), max(duration, 0))), "absolute+exact"]) }
    private func updatePlaybackActivity() {
        let playing = !closed && duration > 0 && !paused && video.string("eof-reached") != "yes"
        if playing && playbackActivity == nil {
            playbackActivity = ProcessInfo.processInfo.beginActivity(options: [.idleDisplaySleepDisabled, .idleSystemSleepDisabled], reason: "DanmakuPlayer 正在播放视频")
        } else if !playing, let playbackActivity {
            ProcessInfo.processInfo.endActivity(playbackActivity); self.playbackActivity = nil
        }
    }
    func setPaused(_ value: Bool) { video.setString(value ? "yes" : "no", property: "pause"); paused = value; sampleOverlayClock(discontinuity: true); updatePlaybackActivity() }
    func setRate(_ value: Double) { video.setNumber(value, property: "speed"); rate = value; sampleOverlayClock(discontinuity: true) }
    func setVolume(_ value: Double) { video.setNumber(value, property: "volume"); volume = value }
    func selectTrack(_ track: MediaTrack) { video.setString(String(track.id), property: track.type == "audio" ? "aid" : "sid"); refreshTracks() }
    func addSubtitle() {
        let panel = NSOpenPanel(); panel.allowedContentTypes = [.init(filenameExtension: "srt")!, .init(filenameExtension: "ass")!, .init(filenameExtension: "vtt")!, .init(filenameExtension: "ssa")!]
        if panel.runModal() == .OK, let url = panel.url { video.command(["sub-add", url.path, "select"]); refreshTracks() }
    }
    func importXML() {
        let panel = NSOpenPanel(); panel.allowedContentTypes = [.xml]
        if panel.runModal() == .OK, let url = panel.url {
            do { try importXML(url: url) } catch { self.error = error.localizedDescription }
        }
    }
    func importXML(url: URL) throws {
        let data = try Data(contentsOf: url)
        let imported = try DanmakuParser.xml(data)
        let target = Paths.cache.appendingPathComponent("local-\(item.id).xml")
        try data.write(to: target, options: .atomic)
        sourceGeneration += 1; commentTask?.cancel(); loading = false
        comments = imported; item.localDanmaku = target.path; item.source = nil
        status = "本地弹幕 · \(comments.count) 条"; persist()
    }
    func replaceItem(_ changed: MediaItem) { item = changed; loadComments() }
    func loadComments(refresh: Bool = false) {
        sourceGeneration += 1; commentTask?.cancel(); loading = false; comments = []; error = ""
        let generation = sourceGeneration
        if let local = item.localDanmaku {
            do { comments = try DanmakuParser.xml(Data(contentsOf: URL(fileURLWithPath: local))); status = "本地弹幕 · \(comments.count) 条" } catch { self.error = error.localizedDescription }; return
        }
        guard let source = item.source else { status = "未关联弹幕"; return }
        loading = true; status = "正在加载弹幕"
        commentTask = Task { [weak self] in
            guard let self else { return }
            do {
                let loaded = try await app.bili.comments(source: source, refresh: refresh)
                guard !Task.isCancelled, sourceGeneration == generation, !closed else { return }
                comments = loaded; status = "\(loaded.count) 条弹幕"
            } catch {
                guard !Task.isCancelled, sourceGeneration == generation, !closed else { return }
                self.error = error.localizedDescription; status = "弹幕加载失败"
                // An explicit refresh failure keeps the previous complete cache available.
                if refresh, let cached = try? await app.bili.comments(source: source) {
                    guard !Task.isCancelled, sourceGeneration == generation, !closed else { return }
                    comments = cached; status = "缓存 · \(cached.count) 条"
                }
            }
            guard !Task.isCancelled, sourceGeneration == generation, !closed else { return }
            loading = false
        }
    }
    func clearCache() {
        sourceGeneration += 1; commentTask?.cancel(); loading = false
        let generation = sourceGeneration, source = item.source, local = item.localDanmaku
        Task {
            do {
                if let source { try await app.bili.clearCache(source: source) }
                guard sourceGeneration == generation, !closed else { return }
                if let local { try FileManager.default.removeItem(atPath: local); item.localDanmaku = nil }
                comments = []; status = "缓存已清除"; persist()
            } catch {
                guard sourceGeneration == generation, !closed else { return }
                self.error = error.localizedDescription
            }
        }
    }
    func persist() {
        guard !closed else { return }
        // Merge into current metadata so an open player cannot overwrite edits made in the library.
        var current = app.items.first { $0.id == item.id } ?? item
        current.position = position; if duration > 0 { current.duration = duration }
        if duration > 0 && position / duration > 0.95 { current.watched = true }
        current.modified = Date(); current.calibration = item.calibration
        current.source = item.source; current.localDanmaku = item.localDanmaku
        app.save(current); lastSave = Date()
    }
    func close() {
        guard !closed else { return }
        persist(); closed = true; sourceGeneration += 1; commentTask?.cancel(); timer?.invalidate(); timer = nil
        updatePlaybackActivity()
        for observer in observers { NSWorkspace.shared.notificationCenter.removeObserver(observer) }; observers = []
        video.shutdown()
    }
}
@MainActor final class PlayerWindowController: NSWindowController, NSWindowDelegate {
    let session: PlayerSession
    init(item: MediaItem, app: AppModel) {
        session = PlayerSession(item: item, app: app)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 700), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = item.title; window.minSize = NSSize(width: 720, height: 480); window.center(); window.isReleasedWhenClosed = false
        super.init(window: window)
        window.delegate = self
        window.contentView = NSHostingView(rootView: PlayerView(session: session).environmentObject(app))
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) unavailable") }
    func windowWillClose(_ notification: Notification) { session.close(); session.app.players.removeValue(forKey: session.item.id) }
}
