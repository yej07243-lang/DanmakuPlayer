import SwiftUI
import AppKit
import PlayerCore

final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var model: AppModel?
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        MainActor.assumeIsolated { if let model { for player in Array(model.players.values) { player.session.close() } } }
        return .terminateNow
    }
    func application(_ application: NSApplication, open urls: [URL]) { Task { @MainActor in model?.importURLs(urls) } }
}
@main struct DanmakuPlayerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = AppModel()
    var body: some Scene {
        WindowGroup("DanmakuPlayer") {
            LibraryView().environmentObject(model).frame(minWidth: 960, minHeight: 640)
                .onAppear { delegate.model = model; NSApp.setActivationPolicy(.regular); NSApp.activate(ignoringOtherApps: true); AcceptanceDriver.startIfRequested(app: model) }
        }.defaultSize(width: 1220, height: 800)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("导入视频…") { model.chooseImport() }.keyboardShortcut("o")
                Button("导入文件夹…") { model.chooseImport(folder: true) }.keyboardShortcut("o", modifiers: [.command, .shift])
                Button("重新扫描") { model.rescan() }.disabled(model.scanning)
            }
            CommandMenu("媒体库") {
                Button("播放选中影片") { if let item = model.selected { model.play(item) } }.disabled(model.selected == nil)
                Button("关联弹幕") { if let item = model.selected { model.showSource(item) } }.disabled(model.selected == nil)
                Button("通过 B 站链接导入弹幕…") { if let item = model.selected { model.showSource(item, link: true) } }.disabled(model.selected == nil)
                Button("编辑资料") { model.editItem = model.selected }.disabled(model.selected == nil)
                Divider()
                Button("导出诊断日志…") { model.exportLogs() }
                Button("打开数据目录") { NSWorkspace.shared.open(Paths.support) }
            }
            CommandGroup(replacing: .help) {
                Button("查看更新包版本…") {
                    let panel = NSOpenPanel(); panel.canChooseDirectories = false; panel.allowedContentTypes = [.applicationBundle]; panel.message = "选择新版本应用以查看版本；更新前请退出播放器，再替换 Applications 中的应用。"
                    if panel.runModal() == .OK, let url = panel.url { let version = Bundle(url: url)?.infoDictionary?["CFBundleShortVersionString"] as? String ?? "未知"; model.message = "所选版本：\(version)。退出后在 Finder 中替换应用，媒体库数据将保留。" }
                }
            }
        }
        Settings {
            Form {
                Toggle("显示弹幕", isOn: $model.settings.enabled)
                Slider(value: $model.settings.fontSize, in: 16...40) { Text("弹幕字号") }
                Button("导出诊断日志…") { model.exportLogs() }
                Button("打开媒体库数据目录") { NSWorkspace.shared.open(Paths.support) }
            }.padding(24).frame(width: 420)
        }
    }
}
