import Foundation
import PlayerCore

final class CoreTests {
    func testFilenameAndEpisode() {
        let item = MediaScanner.parseName("Some.Show.2024.S02E03.1080p")
        XCTAssertEqual(item.title, "Some Show"); XCTAssertEqual(item.year, 2024); XCTAssertEqual(item.season, 2); XCTAssertEqual(item.episode, 3)
        XCTAssertFalse(MediaScanner.parseName("Unknown.mp4").title.isEmpty)
    }
    func testCalibrationAndSeek() {
        let calibration = Calibration(offset: 15, points: [.init(local: 100, source: 120), .init(local: 200, source: 250)])
        XCTAssertEqual(calibration.sourceTime(local: 50), 35)
        XCTAssertEqual(calibration.sourceTime(local: 110), 115)
        XCTAssertEqual(calibration.sourceTime(local: 220), 255)
        XCTAssertEqual(calibration.sourceTime(local: 90), 75)
    }
    func testXMLAndInvalidData() throws {
        let data = Data("<i><d p=\"1.5,1,25,16711680,0,0,a,1\">你好 &amp; 再见</d><d p=\"nan,1,25,1\">invalid</d><d p=\"2,7,25,1\">advanced</d></i>".utf8)
        let items = try DanmakuParser.xml(data)
        XCTAssertEqual(items.count, 1); XCTAssertEqual(items[0].text, "你好 & 再见"); XCTAssertEqual(items[0].time, 1.5); XCTAssertEqual(items[0].color, 0xff0000)
        XCTAssertThrowsError(try DanmakuParser.xml(Data("<i><d>".utf8)))
    }
    func testProtobufAndBounds() throws {
        // elem: id=1, progress=1000, mode=1, color=white, content='hi'.
        let bytes: [UInt8] = [8,1,16,232,7,24,1,40,255,255,255,7,58,2,104,105]
        let entries = try DanmakuParser.protobuf(Data([10,UInt8(bytes.count)] + bytes))
        XCTAssertEqual(entries.count, 1); XCTAssertEqual(entries[0].time, 1); XCTAssertEqual(entries[0].text, "hi")
        XCTAssertThrowsError(try DanmakuParser.protobuf(Data([10,255])))
        XCTAssertThrowsError(try DanmakuParser.protobuf(Data([0])))
        XCTAssertThrowsError(try DanmakuParser.protobuf(Data([10,1,255])))
    }
    func testTimelineDensityPauseAndSeek() {
        let entries = (0..<1000).map { Danmaku(id: String($0), time: Double($0) / 100, text: "comment \($0)") }
        var settings = DanmakuSettings(); settings.maxVisible = 16
        let result = DanmakuTimeline.visible(entries, at: 8, settings: settings)
        XCTAssertLessThanOrEqual(result.count, 16)
        XCTAssertEqual(result.map(\.id), DanmakuTimeline.visible(entries, at: 8, settings: settings).map(\.id))
        XCTAssertTrue(DanmakuTimeline.visible(entries, at: 0, settings: settings).allSatisfy { $0.comment.time == 0 })
        settings.blockedWords = "comment"; XCTAssertTrue(DanmakuTimeline.visible(entries, at: 8, settings: settings).isEmpty)
        settings.enabled = false; XCTAssertTrue(DanmakuTimeline.visible(entries, at: 8, settings: settings).isEmpty)
    }
    func testStoreRescanRelocateConflictAndReopen() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true); defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent("test.sqlite")
        let store = try LibraryStore(url: url)
        var first = MediaItem(path: "/movie/a.mkv", title: "original")
        first.position = 42; first.title = "edited"; first.source = .init(bvid: "BV17x411w7KC", cid: 123, title: "test", page: 1, duration: 100)
        try store.save(first)
        try store.merge([MediaItem(path: first.path, title: "rescan title")])
        let saved = try XCTUnwrap(store.items().first)
        XCTAssertEqual(saved.id, first.id); XCTAssertEqual(saved.title, "edited"); XCTAssertEqual(saved.position, 42); XCTAssertEqual(saved.source, first.source)
        try store.addRoot("/movie"); try store.addRoot("/movie"); XCTAssertEqual(try store.roots(), ["/movie"])
        var second = MediaItem(path: "/movie/b.mkv", title: "second"); try store.save(second)
        second.path = first.path; XCTAssertThrowsError(try store.save(second))
        XCTAssertEqual(try LibraryStore(url: url).items().count, 2)
        first.path = "/moved/a.mkv"; try store.save(first)
        XCTAssertEqual(try store.items().first { $0.id == first.id }?.position, 42)
    }
    func test1000ItemLibrary() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".sqlite")
        defer { try? FileManager.default.removeItem(at: url); try? FileManager.default.removeItem(atPath: url.path + "-wal"); try? FileManager.default.removeItem(atPath: url.path + "-shm") }
        let store = try LibraryStore(url: url)
        let entries = (0..<1000).map { MediaItem(path: "/movies/\($0).mkv", title: "影片 \($0)") }
        try store.merge(entries); try store.merge(entries)
        XCTAssertEqual(try store.items().count, 1000)
    }
    func testSettingsMigrationAndModes() throws {
        let legacy = Data(#"{"enabled":true,"fontSize":32,"opacity":0.6,"maxVisible":24,"area":0.5,"blockedWords":"spoiler"}"#.utf8)
        var settings = try JSONDecoder().decode(DanmakuSettings.self, from: legacy)
        XCTAssertEqual(settings.fontSize, 32); XCTAssertEqual(settings.opacity, 0.6)
        XCTAssertEqual(settings.maxVisible, 24); XCTAssertEqual(settings.area, 0.5); XCTAssertEqual(settings.blockedWords, "spoiler")
        XCTAssertTrue(settings.scrolling && settings.top && settings.bottom)
        settings.scrolling = false; settings.bottom = false
        let entries = [Danmaku(id: "scroll", time: 1, mode: 1, text: "rolling"), Danmaku(id: "top", time: 2, mode: 5, text: "top"), Danmaku(id: "bottom", time: 3, mode: 4, text: "bottom")]
        XCTAssertEqual(DanmakuTimeline.visible(entries, at: 4, settings: settings).map(\.id), ["top"])
        XCTAssertEqual(try JSONDecoder().decode(DanmakuSettings.self, from: JSONEncoder().encode(settings)), settings)
        settings.top = false; XCTAssertTrue(DanmakuTimeline.visible(entries, at: 4, settings: settings).isEmpty)
        settings = DanmakuSettings(); XCTAssertTrue(settings.scrolling && settings.top && settings.bottom)
        XCTAssertEqual(settings.fontSize, 24); XCTAssertEqual(settings.maxVisible, 60)
        XCTAssertEqual(settings.scrollSpeed, 1); XCTAssertEqual(settings.scrollingDuration, 8)
        let old = try JSONDecoder().decode(DanmakuSettings.self, from: legacy)
        XCTAssertEqual(old.scrollSpeed, 1)
        let rolling = [Danmaku(id: "slow", time: 1, text: "slower")]
        XCTAssertTrue(DanmakuTimeline.visible(rolling, at: 10, settings: settings).isEmpty)
        settings.scrollSpeed = 0.5; XCTAssertEqual(settings.scrollingDuration, 16)
        XCTAssertEqual(DanmakuTimeline.visible(rolling, at: 10, settings: settings).map(\.id), ["slow"])
        XCTAssertEqual(try JSONDecoder().decode(DanmakuSettings.self, from: JSONEncoder().encode(settings)).scrollSpeed, 0.5)
        settings.scrollSpeed = 2; XCTAssertEqual(settings.scrollingDuration, 4)
        settings.scrollSpeed = .nan; XCTAssertEqual(settings.scrollingDuration, 8)
    }
    func testPlaybackClockQuantization() {
        var clock = PlaybackClock()
        clock.sample(position: 10, at: 100, rate: 1, paused: false)
        var previous = clock.time(at: 100)
        for frame in 1...600 {
            let host = 100 + Double(frame) / 60
            if frame % 6 == 0 {
                let quantized = floor((10 + host - 100) * 24) / 24
                clock.sample(position: quantized, at: host, rate: 1, paused: false)
            }
            let time = clock.time(at: host)
            XCTAssertTrue(time >= previous - 0.000001)
            XCTAssertTrue(abs(time - (10 + host - 100)) < 0.1)
            previous = time
        }
        clock.sample(position: 3, at: 111, rate: 1, paused: true, discontinuity: true)
        XCTAssertEqual(clock.time(at: 120), 3)
        clock.sample(position: 3, at: 121, rate: 1.5, paused: false)
        XCTAssertTrue(abs(clock.time(at: 121.1) - 3.15) < 0.00001)
    }
    func testNetworkFailuresAndCache() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [FailureProtocol.self]
        let client = BiliClient(session: URLSession(configuration: config))
        let source = BiliSource(bvid: "BV17x411w7KC", cid: 9999999, title: "cache test", page: 1, duration: 10)
        let cached = [Danmaku(id: "cached", time: 1, text: "cached")]
        let url = Paths.cache.appendingPathComponent(source.id + ".json")
        try JSONEncoder().encode(cached).write(to: url)
        XCTAssertEqual(try await client.comments(source: source), cached)
        do { _ = try await client.comments(source: source, refresh: true); XCTAssertTrue(false) } catch { XCTAssertTrue(error.localizedDescription.contains("429")) }
        XCTAssertEqual(try await client.comments(source: source), cached)
        try await client.clearCache(source: source)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        do { _ = try await client.search("query"); XCTAssertTrue(false) } catch { XCTAssertTrue(error.localizedDescription.contains("429")) }
    }
    func testWbiSigningVector() throws {
        let query = try WbiSigning.query(["foo": "114", "bar": "514", "zab": "1919810"], imgKey: "7cd084941338484aae1ad9425b84077c", subKey: "4932caff0ff746eab6f01bf08b70ac45", timestamp: 1702204169)
        XCTAssertEqual(query.first { $0.name == "w_rid" }?.value, "8f6f2b5b3d485fe1886cec6a0be8c5d4")
        XCTAssertEqual(WbiSigning.encode("中 文+&"), "%E4%B8%AD%20%E6%96%87%2B%26")
        XCTAssertThrowsError(try WbiSigning.query([:], imgKey: "bad", subKey: "", timestamp: 0))
    }
    func testLinkParsing() throws {
        XCTAssertEqual(try BiliClient.pageNumber(from: "https://www.bilibili.com/video/BV17x411w7KC/?p=2&share_source=copy_web"), 2)
        XCTAssertTrue(try BiliClient.pageNumber(from: "BV17x411w7KC") == nil)
        XCTAssertThrowsError(try BiliClient.pageNumber(from: "?p=0"))
        XCTAssertThrowsError(try BiliClient.pageNumber(from: "?p=abc"))
        XCTAssertEqual(BiliClient.officialQuery(title: "火线 The Wire", season: 1), "火线 第一季")
        XCTAssertEqual(BiliClient.officialQuery(title: "火线 The Wire", season: 5), "火线 第五季")
        XCTAssertEqual(BiliClient.officialQuery(title: "Breaking Bad", season: 2), "Breaking Bad 第二季")
        XCTAssertEqual(BiliClient.officialQuery(title: "火线 第一季", season: 1), "火线 第一季")
        XCTAssertEqual(BiliClient.officialQuery(title: "头文字D", season: nil), "头文字D")
        XCTAssertEqual(BiliClient.bvid(from: "https://www.bilibili.com/video/BV17x411w7KC/?p=2"), "BV17x411w7KC")
        XCTAssertTrue(BiliClient.bvid(from: "https://example.com") == nil)
        XCTAssertEqual(BiliClient.episodeID(from: "https://www.bilibili.com/bangumi/play/ep830768/?share_source=copy_web"), 830768)
        XCTAssertTrue(BiliClient.episodeID(from: "https://example.com") == nil)
    }
}

private var failureCount = 0
private func XCTAssertEqual<T: Equatable>(_ a: T, _ b: T, file: StaticString = #filePath, line: UInt = #line) { if a != b { failureCount += 1; print("FAIL \(file):\(line) \(a) != \(b)") } }
private func XCTAssertTrue(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) { XCTAssertEqual(value, true, file: file, line: line) }
private func XCTAssertFalse(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) { XCTAssertEqual(value, false, file: file, line: line) }
private func XCTAssertLessThanOrEqual<T: Comparable>(_ a: T, _ b: T, file: StaticString = #filePath, line: UInt = #line) { XCTAssertTrue(a <= b, file: file, line: line) }
private func XCTAssertThrowsError<T>(_ expression: @autoclosure () throws -> T, file: StaticString = #filePath, line: UInt = #line) { do { _ = try expression(); failureCount += 1; print("FAIL expected error \(file):\(line)") } catch {} }
private func XCTUnwrap<T>(_ value: T?) throws -> T { guard let value else { throw PlayerError.message("Expected non-nil") }; return value }
@main struct CoreChecks {
 static func main() async throws {
  let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("danmaku-core-" + UUID().uuidString)
  setenv("DANMAKU_DATA_DIR", temporary.path, 1)
  defer { try? FileManager.default.removeItem(at: temporary) }
  if CommandLine.arguments.contains("--link-import") {
   let client = BiliClient()
   let ordinary = try await client.resolveDanmakuLink("https://www.bilibili.com/video/BV17x411w7KC/?p=2")
   guard ordinary.pages.count == 10, ordinary.selected.page == 2 else { throw PlayerError.message("分 P 链接解析失败") }
   let ordinaryComments = try await client.comments(source: ordinary.selected, refresh: true)
   guard !ordinaryComments.isEmpty else { throw PlayerError.message("普通视频 P2 弹幕为空") }
   let episode = try await client.resolveDanmakuLink("https://www.bilibili.com/bangumi/play/ep830768/")
   guard episode.selected.cid == 500001609499307 else { throw PlayerError.message("番剧链接解析失败") }
   let entries = try await client.comments(source: episode.selected, refresh: true)
   guard !entries.isEmpty else { throw PlayerError.message("真实链接弹幕为空") }
   print("Link import: ordinary P2 of10 comments=\(ordinaryComments.count), episode CID=\(episode.selected.cid), realComments=\(entries.count)")
   do { _ = try await client.resolveDanmakuLink("https://example.com/"); throw PlayerError.message("无效链接被接受") }
   catch { guard error.localizedDescription.contains("请输入") else { throw error } }
   return
  }
  if CommandLine.arguments.contains("--episode-link") {
   let client = BiliClient()
   let candidates = try await client.search("https://www.bilibili.com/bangumi/play/ep830768/")
   guard let candidate = candidates.first, candidate.bvid == "BV1rJaaewEYu",
         let page = try await client.pages(bvid: candidate.bvid).first,
         page.cid == 500001609499307 else { throw PlayerError.message("番剧链接解析与分 P 不符") }
   let comments = try await client.comments(source: page, refresh: true)
   guard !comments.isEmpty else { throw PlayerError.message("番剧弹幕为空") }
   print("Episode link resolved: \(candidate.bvid), cid=\(page.cid), danmaku=\(comments.count)")
   return
  }
  if CommandLine.arguments.contains("--official-network") {
   let client = BiliClient()
   let query = BiliClient.officialQuery(title: "火线 The Wire", season: 1)
   let exact = try await client.search(query, official: true)
   guard exact.count == 1, exact.first?.seasonID == 48164 else { throw PlayerError.message("第一季默认查询返回错误季数") }
   print("Official generated query: \(query), candidates=\(exact.count), seasonID=\(exact.first!.seasonID!)")
   let candidates = try await client.search("火线", official: true)
   guard !candidates.isEmpty else { throw PlayerError.message("官方影视搜索没有结果") }
   for candidate in candidates.prefix(5) {
    let pages = try await client.pages(candidate: candidate)
    print("Official: \(candidate.id) \(candidate.title) episodes=\(pages.count) firstCID=\(pages.first?.cid ?? 0)")
   }
   guard let firstSeason = candidates.first(where: { $0.seasonID == 48164 }) else { throw PlayerError.message("未找到火线第一季") }
   let episodes = try await client.pages(candidate: firstSeason)
   guard episodes.count == 13, let first = episodes.first, first.cid == 500001609499307 else { throw PlayerError.message("官方季集解析不符") }
   let comments = try await client.comments(source: first, refresh: true)
   guard !comments.isEmpty else { throw PlayerError.message("官方弹幕为空") }
   print("Official first season confirmed: episodes=\(episodes.count), cid=\(first.cid), comments=\(comments.count)")
   return
  }
  if CommandLine.arguments.contains("--network") {
   let client = BiliClient()
   do {
    let candidates = try await client.search("电影")
    print("Real keyword candidates=\(candidates.count)")
    if candidates.isEmpty { exit(2) }
   } catch { print("Real keyword failure: \(error.localizedDescription)"); exit(3) }
   return
  }
  let tests = CoreTests()
  tests.testFilenameAndEpisode(); tests.testCalibrationAndSeek()
  try tests.testXMLAndInvalidData(); try tests.testProtobufAndBounds()
  tests.testTimelineDensityPauseAndSeek(); try tests.testStoreRescanRelocateConflictAndReopen()
  try tests.test1000ItemLibrary(); try tests.testLinkParsing(); try tests.testSettingsMigrationAndModes(); tests.testPlaybackClockQuantization(); try await tests.testNetworkFailuresAndCache(); try tests.testWbiSigningVector()
  print("12 core scenarios completed; failures=\(failureCount)")
  if failureCount > 0 { exit(1) }
 }
}

private final class FailureProtocol: URLProtocol {
 override class func canInit(with request: URLRequest) -> Bool { true }
 override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
 override func startLoading() {
  let response = HTTPURLResponse(url: request.url!, statusCode: 429, httpVersion: "HTTP/1.1", headerFields: nil)!
  client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
  client?.urlProtocol(self, didLoad: Data("rate limited".utf8)); client?.urlProtocolDidFinishLoading(self)
 }
 override func stopLoading() {}
}
