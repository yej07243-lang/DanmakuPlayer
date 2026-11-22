import SwiftUI
import AppKit
import PlayerCore

struct LibraryView: View {
    @EnvironmentObject var app: AppModel
    private let sections = ["全部影片", "电影", "剧集", "继续观看", "文件不可用"]
    private let symbols = ["square.grid.2x2", "film", "tv", "play.circle", "externaldrive.badge.exclamationmark"]
    var body: some View {
        NavigationSplitView {
            List(selection: $app.section) {
                Section("媒体库") {
                    ForEach(Array(sections.enumerated()), id: \.offset) { index, name in
                        Label(name, systemImage: symbols[index]).tag(name)
                    }
                }
            }.navigationSplitViewColumnWidth(min: 170, ideal: 190, max: 240)
            .safeAreaInset(edge: .bottom) {
                HStack { Text("\(app.items.count) 部影片").foregroundStyle(.secondary); Spacer(); if app.scanning { ProgressView().controlSize(.small) } }.padding()
            }
        } detail: {
            HSplitView {
                VStack(alignment: .leading, spacing: 0) {
                    HStack {
                        Text(app.section).font(.largeTitle.bold())
                        Spacer()
                        Picker("排序", selection: $app.sort) { Text("标题").tag("标题"); Text("最近播放").tag("最近播放") }.frame(width: 140)
                    }.padding(24)
                    if app.filtered.isEmpty {
                        ContentUnavailableView {
                            Label(app.search.isEmpty ? "添加你的影片" : "没有匹配的影片", systemImage: "film.stack")
                        } actions: {
                            if app.search.isEmpty { Button("导入视频", action: { app.chooseImport() }).buttonStyle(.borderedProminent).disabled(app.scanning) }
                        }.frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        ScrollView {
                            if app.section == "剧集" {
                                let groups = Dictionary(grouping: app.filtered, by: \.title)
                                ForEach(groups.keys.sorted(), id: \.self) { title in
                                    VStack(alignment: .leading, spacing: 16) {
                                        Text(title).font(.title2.bold())
                                        grid(groups[title] ?? [])
                                    }.padding(.bottom, 24)
                                }
                            } else { grid(app.filtered) }
                        }.padding(.horizontal, 24).padding(.bottom, 20)
                    }
                }.frame(minWidth: 460)
                if let item = app.selected {
                    MediaDetail(item: item).frame(width: 280).padding(20)
                }
            }
            .navigationTitle("DanmakuPlayer")
            .searchable(text: $app.search, prompt: "搜索影片或文件名")
            .toolbar {
                ToolbarItemGroup {
                    Button { app.rescan() } label: { Label("重新扫描", systemImage: "arrow.clockwise") }.disabled(app.scanning)
                    Button { app.chooseImport() } label: { Label("导入视频", systemImage: "plus") }.disabled(app.scanning)
                }
            }
            .onDrop(of: [.fileURL], isTargeted: nil) { providers in
                app.importProviders(providers)
                return true
            }
        }
        .sheet(item: $app.sourceItem) { item in SourceSheet(item: item).environmentObject(app) }
        .sheet(item: $app.editItem) { item in EditSheet(item: item).environmentObject(app) }
        .alert("操作未完成", isPresented: Binding(get: { app.message != nil }, set: { if !$0 { app.message = nil } })) { Button("好") { app.message = nil } } message: { Text(app.message ?? "") }
    }
    func grid(_ items: [MediaItem]) -> some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 156, maximum: 220), spacing: 20)], alignment: .leading, spacing: 24) {
            ForEach(items) { item in
                PosterCard(item: item, selected: app.selectedID == item.id)
                    .onTapGesture(count: 2) { app.play(item) }
                    .onTapGesture { app.selectedID = item.id }
                    .focusable()
                    .onKeyPress(.return) { app.play(item); return .handled }
                    .accessibilityAddTraits(.isButton)
                    .accessibilityAction { app.selectedID = item.id; app.play(item) }
                    .contextMenu {
                        Button("播放") { app.play(item) }
                        Button("关联弹幕") { app.showSource(item) }
                        Button("编辑资料") { app.editItem = item }
                        Button("重新定位文件") { app.relocate(item) }
                        Button("在 Finder 中显示") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: item.path)]) }
                        Divider()
                        Button("从媒体库移除", role: .destructive) { app.remove(item) }
                    }
            }
        }
    }
}
struct PosterCard: View {
    let item: MediaItem
    let selected: Bool
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack(alignment: .bottom) {
                Artwork(path: item.artwork).aspectRatio(2.0 / 3, contentMode: .fit)
                if !item.available { Label("文件不可用", systemImage: "externaldrive.badge.exclamationmark").padding(8).frame(maxWidth: .infinity).background(.regularMaterial) }
                else if item.duration > 0 && item.position > 0 { ProgressView(value: min(item.position, item.duration), total: item.duration).tint(.teal).padding(.horizontal, 5).padding(.bottom, 4) }
            }.clipShape(RoundedRectangle(cornerRadius: 10))
                .overlay { RoundedRectangle(cornerRadius: 10).stroke(selected ? Color.accentColor : .clear, lineWidth: 3) }
            Text(item.title).font(.headline).lineLimit(2)
            HStack(spacing: 6) {
                if let episode = item.episode { Text("S\(item.season ?? 1) · E\(episode)") }
                else if let year = item.year { Text(String(year)) }
                Spacer(minLength: 0)
                if item.source != nil || item.localDanmaku != nil { Image(systemName: "text.bubble") }
                if item.watched { Image(systemName: "checkmark.circle.fill") }
            }.font(.caption).foregroundStyle(.secondary)
        }.contentShape(Rectangle()).accessibilityElement(children: .ignore)
        .accessibilityLabel(item.title + (item.episode.map { " 第\($0)集" } ?? ""))
    }
}
struct Artwork: View {
    let path: String?
    var body: some View {
        ZStack {
            Rectangle().fill(Color(nsColor: .controlBackgroundColor))
            if let path, let image = NSImage(contentsOfFile: path) { GeometryReader { geo in Image(nsImage: image).resizable().scaledToFill().frame(width: geo.size.width, height: geo.size.height).clipped() } }
            else { Image(systemName: "film").font(.system(size: 36, weight: .light)).foregroundStyle(.secondary) }
        }
    }
}
struct MediaDetail: View {
    @EnvironmentObject var app: AppModel
    let item: MediaItem
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text(item.title).font(.title2.bold()).textSelection(.enabled)
                HStack { if let year = item.year { Text(String(year)) }; if item.duration > 0 { Text(formatTime(item.duration)) } }.foregroundStyle(.secondary)
                if let episode = item.episode { Text("第 \(item.season ?? 1) 季 · 第 \(episode) 集") }
                Button { app.play(item) } label: { Label(item.position > 0 && !item.watched ? "继续播放 · \(formatTime(item.position))" : "播放", systemImage: "play.fill").frame(maxWidth: .infinity) }.buttonStyle(.borderedProminent).controlSize(.large)
                if !item.summary.isEmpty { Text(item.summary).textSelection(.enabled) }
                Divider()
                Text("弹幕").font(.headline)
                if let source = item.source {
                    Text(source.title).lineLimit(3)
                    Text("P\(source.page) · \(source.bvid)").font(.caption).foregroundStyle(.secondary)
                } else if item.localDanmaku != nil { Label("本地弹幕", systemImage: "doc.text") }
                Button(item.source == nil ? "关联 B 站弹幕" : "更换弹幕来源") { app.showSource(item) }
                Button("通过 B 站链接导入弹幕") { app.showSource(item, link: true) }
                Button("编辑影片资料") { app.editItem = item }
                Divider()
                Text(URL(fileURLWithPath: item.path).lastPathComponent).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                if !item.available { Button("重新定位文件") { app.relocate(item) } }
            }.frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}
struct EditSheet: View {
    @EnvironmentObject var app: AppModel
    @Environment(\.dismiss) var dismiss
    @State var item: MediaItem
    @State private var error: String?
    var body: some View {
        VStack(spacing: 20) {
            Text("编辑影片资料").font(.title2.bold())
            Form {
                TextField("标题", text: $item.title)
                TextField("年份", value: $item.year, format: .number.grouping(.never))
                TextField("季", value: $item.season, format: .number.grouping(.never))
                TextField("集", value: $item.episode, format: .number.grouping(.never))
                TextField("简介", text: $item.summary, axis: .vertical).lineLimit(3...6)
                Toggle("已看完", isOn: $item.watched)
                Button("选择海报") {
                    let panel = NSOpenPanel(); panel.allowedContentTypes = [.jpeg, .png, .heic]
                    if panel.runModal() == .OK, let url = panel.url { item.artwork = url.path }
                }
            }
            if let error { Text(error).foregroundStyle(.red) }
            HStack { Button("取消") { dismiss() }.keyboardShortcut(.cancelAction); Spacer(); Button("保存") { if app.save(item) { dismiss() } else { error = app.message; app.message = nil } }.keyboardShortcut(.defaultAction).disabled(item.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) }
        }.padding(24).frame(width: 480)
    }
}
