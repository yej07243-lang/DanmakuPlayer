import SwiftUI
import PlayerCore

struct SourceSheet: View {
    @EnvironmentObject var app: AppModel
    @Environment(\.dismiss) var dismiss
    let item: MediaItem
    @State private var query = ""
    @State private var byLink = false
    @State private var importTask: Task<Void, Never>?
    @State private var official = false
    @State private var candidates: [BiliCandidate] = []
    @State private var pages: [BiliSource] = []
    @State private var selectedCandidate: String?
    @State private var selected: BiliSource?
    @State private var busy = false
    @State private var error: String?
    @State private var searched = false
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack { Text(byLink ? "通过链接导入弹幕" : "关联 B 站弹幕").font(.title2.bold()); Spacer(); Button("取消") { importTask?.cancel(); dismiss() }.keyboardShortcut(.cancelAction) }
            Picker("导入方式", selection: $byLink) { Text("片名搜索").tag(false); Text("视频链接").tag(true) }.pickerStyle(.segmented).disabled(busy)
            HStack {
                TextField(byLink ? "粘贴 B 站视频／番剧链接或 BV 号" : "片名、BV 号或 B 站链接（含番剧）", text: $query).onSubmit(search).disabled(busy)
                Button(byLink ? "解析链接" : "搜索", action: search).disabled(busy || query.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            if !byLink { Picker("来源类型", selection: $official) {
                Text("普通投稿").tag(false)
                Text("官方影视").tag(true)
            }.pickerStyle(.segmented).disabled(busy)
            .onChange(of: official) { _, value in
                if value && BiliClient.bvid(from: query) == nil && BiliClient.episodeID(from: query) == nil { query = BiliClient.officialQuery(title: item.title, season: item.season) }
                search()
            } }
            if busy { ProgressView().controlSize(.small) }
            if let error { Text(error).foregroundStyle(.red).textSelection(.enabled).fixedSize(horizontal: false, vertical: true) }
            HSplitView {
                List(candidates, selection: $selectedCandidate) { candidate in
                    VStack(alignment: .leading, spacing: 5) {
                        Text(candidate.title).lineLimit(2)
                        Text("\(candidate.author) · \(candidate.duration)").font(.caption).foregroundStyle(.secondary)
                    }.padding(.vertical, 5).frame(maxWidth: .infinity, alignment: .leading).tag(candidate.id)
                }.frame(minWidth: 320).disabled(busy)
                .onChange(of: selectedCandidate) { _, id in
                    guard !byLink, let id, let candidate = candidates.first(where: { $0.id == id }) else { return }
                    busy = true; error = nil; selected = nil; pages = []
                    Task { do { pages = try await app.bili.pages(candidate: candidate) } catch { self.error = error.localizedDescription }; busy = false }
                }
                List(pages, selection: Binding(get: { selected?.id }, set: { id in selected = pages.first { $0.id == id } })) { page in
                    VStack(alignment: .leading, spacing: 6) { Text(official && !byLink ? page.title : "P\(page.page) · \(page.title)"); Text(formatTime(page.duration)).font(.caption).foregroundStyle(.secondary) }.tag(page.id)
                }.frame(minWidth: 230).disabled(busy)
            }.overlay {
                if searched && candidates.isEmpty && !busy && error == nil { ContentUnavailableView("没有找到视频", systemImage: "magnifyingglass") }
            }
            HStack {
                if let selected { Text(official && !byLink ? selected.title : "P\(selected.page) · \(selected.title)").lineLimit(1) }
                Spacer()
                Button(byLink ? "确认导入" : "确认关联") {
                    guard let selected else { return }
                    if !byLink { saveSource(selected); return }
                    busy = true; error = nil
                    importTask = Task {
                        do {
                            let comments = try await app.bili.comments(source: selected)
                            try Task.checkCancellation()
                            guard !comments.isEmpty else { throw PlayerError.message("这个分 P 暂无可用弹幕，请选择其他来源") }
                            saveSource(selected)
                        } catch is CancellationError { } catch { self.error = error.localizedDescription }
                        busy = false
                    }
                }.buttonStyle(.borderedProminent).disabled(selected == nil || busy)
            }
        }.padding(24).frame(width: 760, height: 540)
        .onAppear { byLink = app.sourceLinkMode; resetQuery() }
        .onChange(of: byLink) { _, _ in resetQuery() }
        .onDisappear { importTask?.cancel() }
        .onChange(of: query) { _, _ in
            guard byLink && !busy else { return }
            candidates = []; pages = []; selected = nil; selectedCandidate = nil; searched = false; error = nil
        }
    }
    private func resetQuery() {
        candidates = []; pages = []; selected = nil; selectedCandidate = nil; error = nil; searched = false
        query = byLink ? "" : item.title + (item.year.map { " \($0)" } ?? "") + (item.episode.map { " 第\($0)集" } ?? "")
        if !byLink { search() }
    }
    private func saveSource(_ source: BiliSource) {
        var updated = app.items.first { $0.id == item.id } ?? item
        if let player = app.players[item.id]?.session {
            updated.position = player.position
            if player.duration > 0 { updated.duration = player.duration }
        }
        if updated.source?.id != source.id { updated.calibration = Calibration() }
        updated.source = source; updated.localDanmaku = nil
        guard app.save(updated) else { error = app.message ?? "来源保存失败"; app.message = nil; return }
        app.players[item.id]?.session.replaceItem(updated); dismiss()
    }
    private func search() {
        guard !busy, !query.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        busy = true; error = nil; pages = []; selected = nil; selectedCandidate = nil; candidates = []; searched = true
        Task {
            do {
                if byLink {
                    let result = try await app.bili.resolveDanmakuLink(query)
                    candidates = [result.candidate]; selectedCandidate = result.candidate.id
                    pages = result.pages; selected = result.selected
                } else { candidates = try await app.bili.search(query, official: official) }
            } catch { self.error = error.localizedDescription }
            busy = false
        }
    }
}
