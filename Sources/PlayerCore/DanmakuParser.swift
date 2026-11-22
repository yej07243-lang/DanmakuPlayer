import Foundation
#if canImport(FoundationXML)
import FoundationXML
#endif

public enum DanmakuParser {
    public static func xml(_ data: Data) throws -> [Danmaku] {
        guard data.count <= 64_000_000 else { throw PlayerError.message("弹幕文件超过 64 MB") }
        let reader = XMLDanmakuReader(); let parser = XMLParser(data: data)
        parser.shouldResolveExternalEntities = false; parser.delegate = reader
        guard parser.parse() else { throw PlayerError.message("弹幕 XML 格式损坏") }
        return reader.comments.sorted { $0.time < $1.time }
    }
    public static func protobuf(_ data: Data) throws -> [Danmaku] {
        guard data.count <= 16_000_000 else { throw PlayerError.message("弹幕分段过大") }
        var reader = ProtoReader(data: Array(data)), result: [Danmaku] = []
        while !reader.done {
            let tag = try reader.varint()
            guard tag != 0 else { throw PlayerError.message("弹幕字段无效") }
            if tag >> 3 == 1 && tag & 7 == 2 {
                var elem = ProtoReader(data: try reader.bytes())
                var id: UInt64 = 0, progress: UInt64 = 0, mode: UInt64 = 1, color: UInt64 = 0xffffff, content = ""
                while !elem.done {
                    let field = try elem.varint(), key = field >> 3, wire = field & 7
                    if wire == 0 {
                        let value = try elem.varint()
                        switch key { case 1: id = value; case 2: progress = value; case 3: mode = value; case 5: color = value; default: break }
                    } else if key == 7 && wire == 2 { content = String(decoding: try elem.bytes(), as: UTF8.self) }
                    else { try elem.skip(wire) }
                }
                if [1, 2, 3, 4, 5, 6].contains(mode), !content.isEmpty, content.count <= 500 {
                    result.append(Danmaku(id: String(id), time: Double(progress) / 1000, mode: Int(mode), color: UInt32(truncatingIfNeeded: color), text: content))
                }
            } else { try reader.skip(tag & 7) }
        }
        return result.sorted { $0.time < $1.time }
    }
}
private struct ProtoReader {
    let data: [UInt8]
    var index = 0
    var done: Bool { index >= data.count }
    mutating func varint() throws -> UInt64 {
        var value: UInt64 = 0
        for shift in stride(from: 0, through: 63, by: 7) {
            guard index < data.count else { throw PlayerError.message("弹幕数据截断") }
            let byte = data[index]; index += 1
            if shift == 63 && byte > 1 { throw PlayerError.message("弹幕整数溢出") }
            value |= UInt64(byte & 0x7f) << shift
            if byte & 0x80 == 0 { return value }
        }
        throw PlayerError.message("弹幕整数溢出")
    }
    mutating func bytes() throws -> [UInt8] {
        let length = try varint()
        guard length <= UInt64(data.count - index) else { throw PlayerError.message("弹幕字段越界") }
        defer { index += Int(length) }; return Array(data[index..<index + Int(length)])
    }
    mutating func skip(_ wire: UInt64) throws {
        switch wire {
        case 0: _ = try varint()
        case 1: guard data.count - index >= 8 else { throw PlayerError.message("弹幕字段截断") }; index += 8
        case 2: _ = try bytes()
        case 5: guard data.count - index >= 4 else { throw PlayerError.message("弹幕字段截断") }; index += 4
        default: throw PlayerError.message("弹幕编码不受支持")
        }
    }
}
private final class XMLDanmakuReader: NSObject, XMLParserDelegate {
    var comments: [Danmaku] = []
    var parameters: [String]?, content = ""
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        if elementName == "d" { parameters = attributeDict["p"]?.components(separatedBy: ","); content = "" }
    }
    func parser(_ parser: XMLParser, foundCharacters string: String) { if parameters != nil { content += string } }
    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        guard elementName == "d", let p = parameters else { return }; defer { parameters = nil }
        guard p.count >= 4, let time = Double(p[0]), time.isFinite, time >= 0, time < 100_000_000, let mode = Int(p[1]), [1, 2, 3, 4, 5, 6].contains(mode), let color = UInt32(p[3]), !content.isEmpty, content.count <= 500 else { return }
        comments.append(Danmaku(id: p.count > 7 ? p[7] : "xml-\(comments.count)", time: time, mode: mode, color: color, text: content))
    }
}

public struct ScheduledComment: Identifiable {
    public let comment: Danmaku
    public let elapsed: Double
    public var id: String { comment.id }
}
public enum DanmakuTimeline {
    /// Stateless media-time lookup: seeking, pause, calibration and rate changes cannot accumulate a queue.
    public static func visible(_ comments: [Danmaku], at time: Double, settings: DanmakuSettings, lifetime suppliedLifetime: Double? = nil) -> [ScheduledComment] {
        let lifetime = suppliedLifetime ?? max(4, settings.scrollingDuration)
        guard lifetime.isFinite, lifetime >= 1, lifetime <= 3600 else { return [] }
        guard settings.enabled, time.isFinite else { return [] }
        var low = 0, high = comments.count
        while low < high { let mid = (low + high) / 2; if comments[mid].time < time - lifetime { low = mid + 1 } else { high = mid } }
        let words = settings.blockedWords.components(separatedBy: .newlines).filter { !$0.isEmpty }
        var result: [ScheduledComment] = []
        // ponytail: deterministic density sampling by time bucket; spatial layout handles collision, no pending queue.
        var bucketCount: [Int: Int] = [:]
        let perSecond = max(1, settings.maxVisible / Int(lifetime))
        for entry in comments[low...] {
            if entry.time > time { break }
            guard settings.allows(mode: entry.mode) else { continue }
            let bucket = Int(entry.time.rounded(.down))
            if bucketCount[bucket, default: 0] >= perSecond { continue }
            if words.contains(where: { entry.text.localizedCaseInsensitiveContains($0) }) { continue }
            bucketCount[bucket, default: 0] += 1
            result.append(ScheduledComment(comment: entry, elapsed: time - entry.time))
            if result.count >= max(1, settings.maxVisible) { break }
        }
        return result
    }
}
