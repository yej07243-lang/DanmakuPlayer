import Foundation
import AppKit
import PlayerCore
import MPVBridge

/// Explicit opt-in QA mode. Uses an isolated data directory supplied by the runner.
@MainActor final class AcceptanceDriver {
    static var active: AcceptanceDriver?
    private let app: AppModel
    private var timer: Timer?
    private var started = Date()
    private var checks: [[String: Any]] = []
    private var metrics: [[String: Any]] = []
    private var stage = -1
    private var pausePosition = 0.0
    private var player: PlayerSession?
    private let seconds: Double
    private let fixture: URL
    private let report: URL
    private var maxTickError = 0.0
    private var lastClock: Date?
    private var lastPlaybackPosition: Double?
    private var lastPlaybackAdvance: Date?
    init(app: AppModel, fixture: URL, seconds: Double, report: URL) {
        self.app = app; self.fixture = fixture; self.seconds = seconds; self.report = report
    }
    static func startIfRequested(app: AppModel) {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: "--qa-file"), args.indices.contains(index + 1), active == nil else { return }
        let duration = args.firstIndex(of: "--qa-seconds").flatMap { args.indices.contains($0+1) ? Double(args[$0+1]) : nil } ?? 35
        let output = args.firstIndex(of: "--qa-report").flatMap { args.indices.contains($0+1) ? args[$0+1] : nil } ?? Paths.support.appendingPathComponent("acceptance.json").path
        let driver = AcceptanceDriver(app: app, fixture: URL(fileURLWithPath: args[index+1]), seconds: duration, report: URL(fileURLWithPath: output))
        active = driver; driver.start()
    }
    private func record(_ name: String, _ passed: Bool, _ details: String = "") {
        checks.append(["name": name, "passed": passed, "details": details, "elapsed": Date().timeIntervalSince(started)])
        Diagnostics.log("QA \(name)=\(passed) \(details)")
        flush(complete: false)
    }
    private func start() {
        Task {
            do {
                let file = fixture
                let scanned = try await Task.detached(priority: .utility) {
                    try await MediaScanner.scan([file], fallbackThumbnail: { DPWriteThumbnail($0.path, $1.path) })
                }.value
                try app.store?.merge(scanned); app.reload()
                guard let item = app.items.first(where: { $0.path == fixture.path }) else { throw PlayerError.message("QA 导入失败") }
                record("import", true, "items=\(app.items.count)")
                if !CommandLine.arguments.contains("--qa-format-only") { record("video-thumbnail", item.artwork.map { FileManager.default.fileExists(atPath: $0) } ?? false) }
                var edited = item; edited.summary = "QA edited metadata"; edited.position = 0; edited.watched = false
                app.save(edited)
                try app.store?.merge(scanned); app.reload()
                record("rescan-preserves-edit-and-id", app.items.first(where: { $0.id == item.id })?.summary == edited.summary)
                app.selectedID = edited.id; app.play(edited)
                player = app.players[edited.id]?.session
                if let player {
                    let xml = fixture.deletingLastPathComponent().appendingPathComponent("stress.xml")
                    if FileManager.default.fileExists(atPath: xml.path) {
                        player.comments = try DanmakuParser.xml(Data(contentsOf: xml))
                        player.item.localDanmaku = xml.path; player.status = "QA 合成压力弹幕 · \(player.comments.count) 条"
                        record("xml-100-comments-per-second", player.comments.count == 2000)
                    }
                }
                started = Date()
                timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in MainActor.assumeIsolated { self?.tick() } }
                if let timer { RunLoop.main.add(timer, forMode: .common) }
                // Separate real network probe. Its outcome never changes synthetic content into B站 content.
                Task {
                    guard !CommandLine.arguments.contains("--qa-format-only") else { return }
                    do {
                        let candidates = try await app.bili.search("BV17x411w7KC")
                        let pages = try await app.bili.pages(bvid: "BV17x411w7KC")
                        record("bili-link-and-pages", !candidates.isEmpty && pages.count > 1, "pages=\(pages.count)")
                        if let source = pages.first {
                            let entries = try await app.bili.comments(source: source, refresh: true)
                            record("bili-real-segment", !entries.isEmpty, "comments=\(entries.count) cid=\(source.cid)")
                            let cached = try await app.bili.comments(source: source)
                            record("bili-cache", cached == entries)
                        }
                        do {
                            let results = try await app.bili.search("电影")
                            record("bili-keyword-search", !results.isEmpty, "candidates=\(results.count)")
                        } catch { record("bili-keyword-search", false, error.localizedDescription) }
                    } catch { record("bili-network", false, error.localizedDescription) }
                }
            } catch { record("setup", false, error.localizedDescription); flush(complete: true); NSApp.terminate(nil) }
        }
    }
    private func tick() {
        guard let player else { return }
        let now = Date(), elapsed = now.timeIntervalSince(started)
        let tickGap = lastClock.map { now.timeIntervalSince($0) } ?? 0
        if lastClock != nil { maxTickError = max(maxTickError, abs(tickGap - 0.1)) }; lastClock = now
        let step = Int(elapsed)
        if CommandLine.arguments.contains("--qa-format-only") {
            if step == 2 && stage != step {
                if fixture.lastPathComponent == "broken.mkv" { record("corrupt-file-reported", !player.error.isEmpty, player.error) }
                else { record("format-playback", player.duration > 0 && player.position > 0 && player.error.isEmpty, "duration=\(player.duration) position=\(player.position) hwdec=\(player.video.string("hwdec-current"))") }
            }
            if step != stage && step % 2 == 0 {
                metrics.append(["elapsed": elapsed, "position": player.position, "dropped": player.video.number("frame-drop-count"), "hwdec": player.video.string("hwdec-current")]); flush(complete: false)
            }
            if fixture.lastPathComponent == "broken.mkv" {
                if step == 3 && stage != step { player.error = "" }
                if step == 4 && stage != step { record("engine-error-dismissal-persists", player.error.isEmpty) }
            }
            stage = step
            if elapsed >= seconds { timer?.invalidate(); timer = nil; player.close(); flush(complete: true); NSApp.terminate(nil) }
            return
        }
        if step != stage {
            stage = step
            switch step {
            case 2:
                record("libmpv-open-and-render", player.duration > 0 && player.error.isEmpty && player.position > 0, "duration=\(player.duration) position=\(player.position) error=\(player.error)")
                record("playing-prevents-idle-sleep", player.preventsIdleSleep)
                player.refreshTracks(); record("multiple-audio-tracks", player.tracks.filter { $0.type == "audio" }.count >= 2)
                player.setPaused(true); pausePosition = player.video.number("time-pos")
            case 3:
                let actualPosition = player.video.number("time-pos")
                record("pause", player.video.string("pause") == "yes" && abs(actualPosition - pausePosition) < 0.2, "before=\(pausePosition) after=\(actualPosition)")
                record("pause-releases-idle-sleep", !player.preventsIdleSleep)
                player.setPaused(false); player.seek(8)
            case 4:
                record("seek", player.position >= 7.8 && player.position <= 10)
                player.setRate(1.5)
                if let track = player.tracks.filter({ $0.type == "audio" }).last { player.selectTrack(track) }
                let ass = fixture.deletingLastPathComponent().appendingPathComponent("test.ass")
                player.video.command(["sub-add", ass.path, "select"])
            case 6:
                player.refreshTracks()
                record("rate", abs(player.video.number("speed") - 1.5) < 0.01)
                record("audio-selection", player.tracks.filter { $0.type == "audio" }.last?.selected == true)
                record("ass-external-subtitle", player.tracks.contains { $0.type == "sub" && $0.selected })
                player.item.calibration.offset = 2
                player.item.calibration.points = [.init(local: 5, source: 10)]
                record("piecewise-calibration", player.item.calibration.sourceTime(local: 8) == 11)
                player.persist()
                record("saved-progress", (app.items.first { $0.id == player.item.id }?.position ?? 0) > 0)
                player.video.setString("inf", property: "loop-file"); player.setRate(1); player.item.calibration = Calibration()
                player.seek(0)
            case 9:
                player.persist()
                let previousPosition = player.position
                let identity = player.item.id
                app.players[identity]?.close()
                record("close-releases-idle-sleep", !player.preventsIdleSleep)
                if let restored = app.items.first(where: { $0.id == identity }) {
                    app.play(restored); self.player = app.players[identity]?.session
                    record("close-and-reopen", self.player != nil, "savedPosition=\(previousPosition)")
                }
            case 11:
                record("resume-position", player.position > 1, "position=\(player.position)")
                player.video.setString("inf", property: "loop-file")
            case 12:
                player.item.localDanmaku = nil
                player.item.source = BiliSource(bvid: "BV17x411w7KC", cid: 279786, title: "QA remote source", page: 1, duration: 199)
                player.loadComments(refresh: true)
                Task {
                    try? await Task.sleep(nanoseconds: 100_000_000)
                    do { try player.importXML(url: fixture.deletingLastPathComponent().appendingPathComponent("stress.xml")) }
                    catch { record("xml-replacement", false, error.localizedDescription) }
                }
            case 14:
                record("xml-wins-pending-network", player.item.source == nil && player.comments.count == 2000 && !player.loading)
                player.loading = true; player.loadComments()
                record("local-replacement-clears-spinner", !player.loading && player.comments.count == 2000)
            case 16:
                let toRemove = player.item
                app.removeFromLibrary(toRemove)
                record("remove-closes-active-player", app.players[toRemove.id] == nil)
                record("remove-deletes-record", !app.items.contains { $0.id == toRemove.id })
                player.persist()
                record("closed-player-cannot-resurrect-row", !app.items.contains { $0.id == toRemove.id })
                app.save(toRemove); app.play(toRemove); self.player = app.players[toRemove.id]?.session
            case 18:
                player.video.setString("inf", property: "loop-file")
            case 20:
                var conflict = player.item; conflict.id = UUID().uuidString
                let saved = app.save(conflict)
                record("persistence-failure-reported", !saved && app.message != nil)
                app.message = nil
            default: break
            }
            if step % 10 == 0 {
                metrics.append(["elapsed": elapsed, "position": player.position, "dropped": player.video.number("frame-drop-count"), "decoderDropped": player.video.number("decoder-frame-drop-count"), "hwdec": player.video.string("hwdec-current"), "maxMainTickJitter": maxTickError])
                flush(complete: false)
            }
        }
        if elapsed >= 20 {
            if let previous = lastPlaybackPosition {
                if abs(player.position - previous) > 0.05 {
                    lastPlaybackPosition = player.position; lastPlaybackAdvance = now
                }
            } else {
                lastPlaybackPosition = player.position; lastPlaybackAdvance = now
            }
            if tickGap > 10 || now.timeIntervalSince(lastPlaybackAdvance ?? now) > 5 {
                record("continuous-playback", false, "elapsed=\(elapsed) position=\(player.position) tickGap=\(tickGap)")
                timer?.invalidate(); timer = nil; player.close(); flush(complete: true); NSApp.terminate(nil)
                return
            }
        }
        if elapsed >= seconds {
            record("continuous-playback", true, "seconds=\(elapsed)")
            record("duration-completed", true, "seconds=\(elapsed)")
            player.persist()
            do {
                let persisted = try LibraryStore().items().first { $0.id == player.item.id }
                record("database-reopen-progress", abs((persisted?.position ?? -100) - player.position) < 0.5)
            } catch { record("database-reopen-progress", false, error.localizedDescription) }
            timer?.invalidate(); timer = nil; player.close(); flush(complete: true); NSApp.terminate(nil)
        }
    }
    private func flush(complete: Bool) {
        let object: [String: Any] = ["complete": complete, "started": ISO8601DateFormatter().string(from: started), "elapsed": Date().timeIntervalSince(started), "checks": checks, "metrics": metrics, "os": ProcessInfo.processInfo.operatingSystemVersionString, "note": "Synthetic fixture tests and real network tests are separate. This does not certify matching-content sync, another user account, sleep, or notarization."]
        if let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) { try? data.write(to: report, options: .atomic) }
    }
}
