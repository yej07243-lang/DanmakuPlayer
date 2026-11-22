import SwiftUI
import AppKit
import UniformTypeIdentifiers
import PlayerCore
import MPVBridge

@MainActor final class AppModel: ObservableObject {
    @Published var items: [MediaItem] = []
    @Published var selectedID: String?
    @Published var section = "全部影片"
    @Published var search = ""
    @Published var sort = "标题"
    @Published var scanning = false
    @Published var message: String?
    @Published var sourceItem: MediaItem?
    var sourceLinkMode = false
    @Published var editItem: MediaItem?
    @Published var settings = DanmakuSettings() { didSet { if let data = try? JSONEncoder().encode(settings) { UserDefaults.standard.set(data, forKey: "danmaku-settings") } } }
    let store: LibraryStore?
    let bili = BiliClient()
    var players: [String: PlayerWindowController] = [:]
    var scanTask: Task<Void, Never>?
    init() {
        do { store = try LibraryStore() } catch { store = nil; message = error.localizedDescription }
        if let data = UserDefaults.standard.data(forKey: "danmaku-settings"), let value = try? JSONDecoder().decode(DanmakuSettings.self, from: data) { settings = value }
        reload()
    }
    var selected: MediaItem? { items.first { $0.id == selectedID } }
    var filtered: [MediaItem] {
        let list = items.filter { item in
            let sectionMatch = section == "全部影片" || (section == "电影" && item.episode == nil) || (section == "剧集" && item.episode != nil) || (section == "继续观看" && item.position > 0 && !item.watched) || (section == "文件不可用" && !item.available)
            return sectionMatch && (search.isEmpty || item.title.localizedCaseInsensitiveContains(search) || URL(fileURLWithPath: item.path).lastPathComponent.localizedCaseInsensitiveContains(search))
        }
        return list.sorted {
            if sort == "最近播放" { return $0.modified > $1.modified }
            if $0.title != $1.title { return $0.title.localizedStandardCompare($1.title) == .orderedAscending }
            if $0.season != $1.season { return ($0.season ?? 0) < ($1.season ?? 0) }
            return ($0.episode ?? 0) < ($1.episode ?? 0)
        }
    }
    func reload() {
        do {
            items = try store?.items() ?? []
            for i in items.indices { items[i].available = FileManager.default.fileExists(atPath: items[i].path) }
        } catch { report(error) }
    }
    @discardableResult func save(_ item: MediaItem) -> Bool {
        do {
            guard let store else { throw PlayerError.message("媒体库不可用，请检查数据目录权限") }
            try store.save(item); reload(); return true
        } catch { report(error); return false }
    }
    func showSource(_ item: MediaItem, link: Bool = false) {
        sourceLinkMode = link; sourceItem = item
        players[item.id]?.session.showSettings = false
        players[item.id]?.window?.orderBack(nil)
        NSApp.windows.first { $0.title == "DanmakuPlayer" }?.makeKeyAndOrderFront(nil)
    }
    func report(_ error: Error) { message = error.localizedDescription; Diagnostics.log("operation failed: \(error.localizedDescription)") }
    func chooseImport(folder: Bool = false) {
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = !folder; panel.allowsMultipleSelection = true; panel.prompt = "导入"
        if panel.runModal() == .OK { importURLs(panel.urls) }
    }
    func importURLs(_ urls: [URL]) {
        guard !scanning else { message = "正在扫描，请完成后再导入"; return }
        scanning = true
        scanTask = Task {
            do {
                let store = self.store
                try await Task.detached(priority: .utility) {
                    for url in urls { try store?.addRoot(url.standardizedFileURL.resolvingSymlinksInPath().path) }
                    let scanned = try await MediaScanner.scan(urls, fallbackThumbnail: { DPWriteThumbnail($0.path, $1.path) })
                    try Task.checkCancellation(); try store?.merge(scanned)
                }.value
                reload()
            } catch { report(error) }
            scanning = false
        }
    }
    func importProviders(_ providers: [NSItemProvider]) {
        Task {
            var urls: [URL] = []
            for provider in providers {
                let url: URL? = await withCheckedContinuation { continuation in
                    _ = provider.loadObject(ofClass: URL.self) { value, _ in continuation.resume(returning: value) }
                }
                if let url { urls.append(url) }
            }
            importURLs(urls)
        }
    }
    func rescan() {
        do { importURLs(try store?.roots().map { URL(fileURLWithPath: $0) } ?? []) } catch { report(error) }
    }
    func relocate(_ item: MediaItem) {
        let panel = NSOpenPanel(); panel.canChooseFiles = true; panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        var changed = item; changed.path = url.standardizedFileURL.resolvingSymlinksInPath().path; changed.available = true
        do { try store?.save(changed); try store?.addRoot(changed.path); reload() } catch { report(error) }
    }
    func remove(_ item: MediaItem) {
        let alert = NSAlert(); alert.messageText = "从媒体库移除“\(item.title)”？"; alert.informativeText = "原始视频文件会保留。"; alert.addButton(withTitle: "移除"); alert.addButton(withTitle: "取消")
        if alert.runModal() == .alertFirstButtonReturn { removeFromLibrary(item) }
    }
    func removeFromLibrary(_ item: MediaItem) {
        // Closing first flushes progress; deleting last prevents a live timer from resurrecting the row.
        players[item.id]?.close()
        do { try store?.remove(id: item.id); reload() } catch { report(error) }
    }
    func play(_ item: MediaItem) {
        guard FileManager.default.fileExists(atPath: item.path) else { relocate(item); return }
        if let window = players[item.id] { window.showWindow(nil); window.window?.makeKeyAndOrderFront(nil); return }
        for controller in Array(players.values) { controller.close() }
        let controller = PlayerWindowController(item: item, app: self)
        players[item.id] = controller; controller.showWindow(nil); controller.window?.makeKeyAndOrderFront(nil)
    }
    func exportLogs() {
        let panel = NSSavePanel(); panel.nameFieldStringValue = "DanmakuPlayer-diagnostics.log"
        if panel.runModal() == .OK, let url = panel.url {
            do { let data = (try? Data(contentsOf: Paths.support.appendingPathComponent("diagnostics.log"))) ?? Data("暂无日志\n".utf8); try data.write(to: url, options: .atomic) } catch { report(error) }
        }
    }
}
func formatTime(_ value: Double) -> String {
    guard value.isFinite, value >= 0 else { return "0:00" }
    let n = Int(min(value, 10_000_000)); return n >= 3600 ? String(format: "%d:%02d:%02d", n / 3600, n / 60 % 60, n % 60) : String(format: "%d:%02d", n / 60, n % 60)
}
