import Foundation

public actor BiliClient {
    private let session: URLSession
    private var guestCookies: [HTTPCookie] = []
    private var searchKeys: (img: String, sub: String, date: Date)?
    public init(session supplied: URLSession? = nil) {
        if let supplied { session = supplied; return }
        let config = URLSessionConfiguration.ephemeral; config.timeoutIntervalForRequest = 20; config.timeoutIntervalForResource = 60
        config.httpAdditionalHeaders = ["User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", "Referer": "https://www.bilibili.com/"]
        session = URLSession(configuration: config)
    }
    private func request(_ path: String, query: [URLQueryItem]) async throws -> Data {
        var parts = URLComponents(string: "https://api.bilibili.com\(path)")!; parts.percentEncodedQuery = query.map { WbiSigning.encode($0.name) + "=" + WbiSigning.encode($0.value ?? "") }.joined(separator: "&")
        var request = URLRequest(url: parts.url!)
        if !guestCookies.isEmpty { for (name, value) in HTTPCookie.requestHeaderFields(with: guestCookies) { request.setValue(value, forHTTPHeaderField: name) } }
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw PlayerError.message("B 站响应无效") }
        guard response.statusCode == 200 else { Diagnostics.log("bilibili endpoint=\(path) http=\(response.statusCode)"); throw PlayerError.message("B 站请求失败（HTTP \(response.statusCode)），请稍后重试或使用本地弹幕") }
        guard data.count <= 16_000_000 else { throw PlayerError.message("B 站响应过大") }
        return data
    }
    private func object(_ path: String, query: [URLQueryItem], payloadKey: String = "data") async throws -> [String: Any] {
        let data = try await request(path, query: query)
        guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw PlayerError.message("B 站响应无法解析") }
        let code = obj["code"] as? Int ?? -1
        guard code == 0 else {
            Diagnostics.log("bilibili endpoint=\(path) code=\(code)")
            throw PlayerError.message("B 站暂未提供数据（\(code)）：\(obj["message"] as? String ?? "访问受限")。可稍后重试或导入本地 XML。")
        }
        guard let payload = obj[payloadKey] as? [String: Any] else { throw PlayerError.message("B 站响应缺少内容") }
        if payload["v_voucher"] != nil { throw PlayerError.message("B 站搜索需要网站验证。请在浏览器搜索后粘贴视频链接，或稍后重试。") }
        return payload
    }
    public static func bvid(from input: String) -> String? {
        guard let regex = try? NSRegularExpression(pattern: "BV[0-9A-Za-z]{10}"), let match = regex.firstMatch(in: input, range: NSRange(input.startIndex..., in: input)), let range = Range(match.range, in: input) else { return nil }
        return String(input[range])
    }
    public static func episodeID(from input: String) -> Int? {
        guard let regex = try? NSRegularExpression(pattern: "(?:^|/)ep([0-9]+)(?:[/?#]|$)"),
              let match = regex.firstMatch(in: input, range: NSRange(input.startIndex..., in: input)),
              let range = Range(match.range(at: 1), in: input) else { return nil }
        return Int(input[range])
    }
    public static func pageNumber(from input: String) throws -> Int? {
        let regex = try NSRegularExpression(pattern: "[?&]p=([^&#\\s]+)")
        guard let match = regex.firstMatch(in: input, range: NSRange(input.startIndex..., in: input)), let range = Range(match.range(at: 1), in: input) else { return nil }
        guard let page = Int(input[range]), page > 0, page <= 10000 else { throw PlayerError.message("链接的分 P 编号无效") }
        return page
    }
    public func resolveDanmakuLink(_ input: String) async throws -> (candidate: BiliCandidate, pages: [BiliSource], selected: BiliSource) {
        var query = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if let range = query.range(of: "https?://(?:www\\.)?b23\\.tv/[A-Za-z0-9]+", options: .regularExpression), let url = URL(string: String(query[range]).replacingOccurrences(of: "http://", with: "https://")) {
            var request = URLRequest(url: url); request.httpMethod = "HEAD"
            let (_, response) = try await session.data(for: request, delegate: BiliLinkRedirectGuard())
            guard let response = response as? HTTPURLResponse, (200..<400).contains(response.statusCode),
                  let final = response.url, BiliLinkRedirectGuard.allowed(final), final.host?.hasSuffix("bilibili.com") == true else { throw PlayerError.message("短链接解析失败，请打开 B 站网页后复制完整视频链接") }
            query = final.absoluteString
        }
        guard Self.bvid(from: query) != nil || Self.episodeID(from: query) != nil else { throw PlayerError.message("请输入 B 站视频链接、番剧 ep 链接或 BV 号") }
        guard let candidate = try await search(query).first else { throw PlayerError.message("链接没有可用的视频") }
        let pages = try await pages(candidate: candidate)
        let requested = try Self.pageNumber(from: query) ?? 1
        guard let selected = pages.first(where: { $0.page == requested }) else { throw PlayerError.message("该视频没有 P\(requested)，请检查链接") }
        return (candidate, pages, selected)
    }
    public static func officialQuery(title: String, season: Int?) -> String {
        var query = title.trimmingCharacters(in: .whitespacesAndNewlines)
        // ponytail: filename bilingual aliases use spaces; only remove a trailing multiword Latin alias.
        // Single-word or mixed titles stay intact; users can edit the query for ambiguous metadata.
        if query.range(of: "\\p{Han}", options: .regularExpression) != nil {
            query = query.replacingOccurrences(of: "\\s+[A-Za-z][A-Za-z'’.-]*(?:\\s+[A-Za-z][A-Za-z'’.-]*)+$", with: "", options: .regularExpression)
        }
        if let season, season > 0,
           query.range(of: "第[^\\s]*季", options: .regularExpression) == nil {
            let formatter = NumberFormatter(); formatter.locale = Locale(identifier: "zh_CN"); formatter.numberStyle = .spellOut
            query += " 第\(formatter.string(from: NSNumber(value: season)) ?? String(season))季"
        }
        return query
    }
    public func search(_ keyword: String, official: Bool = false) async throws -> [BiliCandidate] {
        if let episode = Self.episodeID(from: keyword) {
            let season = try await object("/pgc/view/web/season", query: [.init(name: "ep_id", value: String(episode))], payloadKey: "result")
            guard let entry = (season["episodes"] as? [[String: Any]])?.first(where: { $0["id"] as? Int == episode }),
                  let id = entry["bvid"] as? String, Self.bvid(from: id) == id else { throw PlayerError.message("番剧集数暂不可用，请稍后重试") }
            return try await search(id)
        }
        if let id = Self.bvid(from: keyword) {
            let data = try await object("/x/web-interface/view", query: [.init(name: "bvid", value: id)])
            return [BiliCandidate(bvid: id, title: data["title"] as? String ?? id, author: (data["owner"] as? [String: Any])?["name"] as? String ?? "", duration: String(data["duration"] as? Int ?? 0) + " 秒")]
        }
        let keys = try await prepareSearch()
        let query = try WbiSigning.query(["search_type": official ? "media_ft" : "video", "keyword": keyword, "page": "1"], imgKey: keys.img, subKey: keys.sub, timestamp: Int(Date().timeIntervalSince1970))
        let data = try await object("/x/web-interface/wbi/search/type", query: query)
        return (data["result"] as? [[String: Any]] ?? []).compactMap { row in
            if official {
                guard let seasonID = row["season_id"] as? Int else { return nil }
                let title = (row["title"] as? String ?? "官方影视").replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression)
                return BiliCandidate(bvid: "", title: title, author: "B站官方影视", duration: row["index_show"] as? String ?? "", seasonID: seasonID)
            }
            guard let id = row["bvid"] as? String, Self.bvid(from: id) == id else { return nil }
            let raw = row["title"] as? String ?? id
            let title = raw.replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression).replacingOccurrences(of: "&amp;", with: "&").replacingOccurrences(of: "&quot;", with: "\"")
            return BiliCandidate(bvid: id, title: title, author: row["author"] as? String ?? "", duration: row["duration"] as? String ?? "")
        }
    }
    private func prepareSearch() async throws -> (img: String, sub: String, date: Date) {
        if let searchKeys, Date().timeIntervalSince(searchKeys.date) < 21_600 { return searchKeys }
        let guest = try await object("/x/frontend/finger/spi", query: [])
        for (field, name) in [("b_3", "buvid3"), ("b_4", "buvid4")] {
            if let value = guest[field] as? String, let cookie = HTTPCookie(properties: [.domain: ".bilibili.com", .path: "/", .name: name, .value: value, .secure: "TRUE"]) { guestCookies.removeAll { $0.name == name }; guestCookies.append(cookie); session.configuration.httpCookieStorage?.setCookie(cookie) }
        }
        // nav returns -101 for anonymous clients while still supplying public WBI keys.
        let raw = try await request("/x/web-interface/nav", query: [])
        guard let obj = try JSONSerialization.jsonObject(with: raw) as? [String: Any],
              let payload = obj["data"] as? [String: Any], let images = payload["wbi_img"] as? [String: Any],
              let img = images["img_url"] as? String, let sub = images["sub_url"] as? String,
              let imgURL = URL(string: img), let subURL = URL(string: sub) else { throw PlayerError.message("B 站搜索暂不可用，可粘贴视频链接关联") }
        let keys = (imgURL.deletingPathExtension().lastPathComponent, subURL.deletingPathExtension().lastPathComponent, Date())
        searchKeys = keys
        return keys
    }
    public func pages(candidate: BiliCandidate) async throws -> [BiliSource] {
        guard let seasonID = candidate.seasonID else { return try await pages(bvid: candidate.bvid) }
        let season = try await object("/pgc/view/web/season", query: [.init(name: "season_id", value: String(seasonID))], payloadKey: "result")
        return (season["episodes"] as? [[String: Any]] ?? []).enumerated().compactMap { index, episode in
            guard let bvid = episode["bvid"] as? String, Self.bvid(from: bvid) == bvid,
                  let cid = episode["cid"] as? Int64 else { return nil }
            let number = episode["title"] as? String ?? String(index + 1)
            let title = episode["long_title"] as? String ?? ""
            return BiliSource(bvid: bvid, cid: cid, title: "第\(number)集 \(title)".trimmingCharacters(in: .whitespaces), page: index + 1, duration: (episode["duration"] as? Double ?? 0) / 1000)
        }
    }
    public func pages(bvid: String) async throws -> [BiliSource] {
        let data = try await object("/x/web-interface/view", query: [.init(name: "bvid", value: bvid)])
        return (data["pages"] as? [[String: Any]] ?? []).compactMap { page in
            guard let cid = page["cid"] as? Int64 else { return nil }
            return BiliSource(bvid: bvid, cid: cid, title: page["part"] as? String ?? "", page: page["page"] as? Int ?? 1, duration: page["duration"] as? Double ?? 0)
        }
    }
    public func comments(source: BiliSource, refresh: Bool = false) async throws -> [Danmaku] {
        let url = Paths.cache.appendingPathComponent(source.id + ".json")
        if !refresh, let data = try? Data(contentsOf: url), let cached = try? JSONDecoder().decode([Danmaku].self, from: data) { return cached }
        let count = max(1, Int(ceil(source.duration / 360)))
        guard count <= 1000 else { throw PlayerError.message("视频时长超出弹幕加载范围") }
        var entries: [Danmaku] = []
        for segment in 1...count {
            try Task.checkCancellation()
            let data = try await request("/x/v2/dm/web/seg.so", query: [.init(name: "type", value: "1"), .init(name: "oid", value: String(source.cid)), .init(name: "segment_index", value: String(segment))])
            if let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let code = object["code"] as? Int, code != 0 { throw PlayerError.message("弹幕访问受限（\(code)），可使用缓存或本地 XML") }
            entries += try DanmakuParser.protobuf(data)
            if segment < count { try await Task.sleep(nanoseconds: 150_000_000) }
        }
        var seen = Set<String>()
        entries = entries.filter { seen.insert($0.id).inserted }.sorted { $0.time < $1.time }
        // Atomic complete-cache replacement: a failed refresh never destroys the old cache.
        try JSONEncoder().encode(entries).write(to: url, options: .atomic)
        Diagnostics.log("danmaku loaded segments=\(count) comments=\(entries.count)")
        return entries
    }
    public func clearCache(source: BiliSource) throws {
        let url = Paths.cache.appendingPathComponent(source.id + ".json")
        if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
    }
}

private final class BiliLinkRedirectGuard: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    static func allowed(_ url: URL) -> Bool {
        guard url.scheme == "https", url.port == nil || url.port == 443, let host = url.host?.lowercased() else { return false }
        return host == "b23.tv" || host == "www.b23.tv" || host == "bilibili.com" || host.hasSuffix(".bilibili.com")
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(request.url.map(Self.allowed) == true ? request : nil)
    }
}
