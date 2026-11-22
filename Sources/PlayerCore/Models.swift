import Foundation

public struct MediaItem: Codable, Identifiable, Equatable, Sendable {
    public var id = UUID().uuidString
    public var path: String
    public var title: String
    public var year: Int?
    public var season: Int?
    public var episode: Int?
    public var duration: Double = 0
    public var position: Double = 0
    public var watched = false
    public var artwork: String?
    public var summary = ""
    public var available = true
    public var source: BiliSource?
    public var localDanmaku: String?
    public var calibration = Calibration()
    public var modified = Date()
    public init(path: String, title: String) { self.path = path; self.title = title }
}
public struct BiliSource: Codable, Equatable, Sendable, Identifiable {
    public var bvid: String
    public var cid: Int64
    public var title: String
    public var page: Int
    public var duration: Double
    public var id: String { "\(bvid)-\(cid)" }
    public init(bvid: String, cid: Int64, title: String, page: Int, duration: Double) {
        self.bvid = bvid; self.cid = cid; self.title = title; self.page = page; self.duration = duration
    }
}
public struct BiliCandidate: Identifiable, Sendable {
    public let bvid: String, title: String, author: String, duration: String
    public let seasonID: Int?
    public var id: String { seasonID.map { "ss\($0)" } ?? bvid }
    public init(bvid: String, title: String, author: String, duration: String, seasonID: Int? = nil) {
        self.bvid = bvid; self.title = title; self.author = author; self.duration = duration; self.seasonID = seasonID
    }
}
public struct CalibrationPoint: Codable, Equatable, Identifiable, Sendable {
    public var local: Double
    public var source: Double
    public var id: Double { local }
    public init(local: Double, source: Double) { self.local = local; self.source = source }
}
public struct Calibration: Codable, Equatable, Sendable {
    /// Positive offset means source comments appear later on the local timeline.
    public var offset: Double = 0
    public var points: [CalibrationPoint] = []
    public init(offset: Double = 0, points: [CalibrationPoint] = []) { self.offset = offset; self.points = points }
    public func sourceTime(local: Double) -> Double {
        let base = points.filter { $0.local <= local }.max { $0.local < $1.local }
        return local - offset + (base.map { $0.source - $0.local } ?? 0)
    }
}
public struct Danmaku: Codable, Identifiable, Equatable, Sendable {
    public var id: String
    public var time: Double
    public var mode: Int
    public var color: UInt32
    public var text: String
    public init(id: String, time: Double, mode: Int = 1, color: UInt32 = 0xffffff, text: String) {
        self.id = id; self.time = time; self.mode = mode; self.color = color; self.text = text
    }
}
public struct DanmakuSettings: Codable, Equatable, Sendable {
    public var enabled = true
    public var fontSize: Double = 24
    public var opacity: Double = 0.9
    public var maxVisible = 60
    public var area: Double = 0.65
    public var blockedWords = ""
    public var scrolling = true
    public var top = true
    public var bottom = true
    public var scrollSpeed: Double = 1
    public var scrollingDuration: Double { 8 / (scrollSpeed.isFinite ? min(2, max(0.5, scrollSpeed)) : 1) }
    public init() {}
    public func allows(mode: Int) -> Bool { mode == 4 ? bottom : mode == 5 ? top : scrolling }
    private enum CodingKeys: String, CodingKey { case enabled, fontSize, opacity, maxVisible, area, blockedWords, scrolling, top, bottom, scrollSpeed }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        enabled = try values.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        fontSize = try values.decodeIfPresent(Double.self, forKey: .fontSize) ?? 24
        opacity = try values.decodeIfPresent(Double.self, forKey: .opacity) ?? 0.9
        maxVisible = try values.decodeIfPresent(Int.self, forKey: .maxVisible) ?? 60
        area = try values.decodeIfPresent(Double.self, forKey: .area) ?? 0.65
        blockedWords = try values.decodeIfPresent(String.self, forKey: .blockedWords) ?? ""
        scrolling = try values.decodeIfPresent(Bool.self, forKey: .scrolling) ?? true
        top = try values.decodeIfPresent(Bool.self, forKey: .top) ?? true
        bottom = try values.decodeIfPresent(Bool.self, forKey: .bottom) ?? true
        scrollSpeed = try values.decodeIfPresent(Double.self, forKey: .scrollSpeed) ?? 1
    }
}
public enum PlayerError: LocalizedError {
    case message(String)
    public var errorDescription: String? { switch self { case .message(let s): return s } }
}
public enum Paths {
    public static var support: URL {
        let env = ProcessInfo.processInfo.environment["DANMAKU_DATA_DIR"]
        let url = env.map { URL(fileURLWithPath: $0) } ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("DanmakuPlayer")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    public static var cache: URL {
        let url = support.appendingPathComponent("DanmakuCache")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
public enum Diagnostics {
    private static let lock = NSLock()
    public static func log(_ text: String) {
        lock.lock(); defer { lock.unlock() }
        let url = Paths.support.appendingPathComponent("diagnostics.log")
        if let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 2_000_000 { try? FileManager.default.removeItem(at: url) }
        if !FileManager.default.fileExists(atPath: url.path) { FileManager.default.createFile(atPath: url.path, contents: nil) }
        guard let h = try? FileHandle(forWritingTo: url) else { return }
        defer { try? h.close() }
        _ = try? h.seekToEnd(); try? h.write(contentsOf: Data("\(ISO8601DateFormatter().string(from: Date())) \(text)\n".utf8))
    }
}
