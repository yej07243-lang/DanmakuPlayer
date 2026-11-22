import Foundation
#if canImport(FoundationXML)
import FoundationXML
#endif
import AVFoundation
import AppKit

public enum MediaScanner {
    public static let extensions: Set<String> = ["mp4", "mkv", "mov", "avi", "webm", "m4v", "ts", "m2ts", "flv", "wmv", "mpg", "mpeg"]
    public static func parseName(_ name: String) -> MediaItem {
        var item = MediaItem(path: "", title: name)
        let full = NSRange(name.startIndex..., in: name)
        if let r = try? NSRegularExpression(pattern: "(?i)[ ._-]*s(\\d{1,2})e(\\d{1,3})"), let m = r.firstMatch(in: name, range: full) {
            item.season = Range(m.range(at: 1), in: name).flatMap { Int(name[$0]) }
            item.episode = Range(m.range(at: 2), in: name).flatMap { Int(name[$0]) }
            if let range = Range(m.range, in: name) { item.title = String(name[..<range.lowerBound]) }
        }
        if let r = try? NSRegularExpression(pattern: "(?:^|[ ._(\\[])(19\\d{2}|20\\d{2})(?=$|[ ._)\\]])"), let m = r.firstMatch(in: item.title, range: NSRange(item.title.startIndex..., in: item.title)), let yearRange = Range(m.range(at: 1), in: item.title), let range = Range(m.range, in: item.title) {
            item.year = Int(item.title[yearRange]); item.title = String(item.title[..<range.lowerBound])
        }
        item.title = item.title.replacingOccurrences(of: ".", with: " ").replacingOccurrences(of: "_", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        if item.title.isEmpty { item.title = name }
        return item
    }
    public static func files(in urls: [URL]) -> [URL] {
        var found = Set<String>()
        for url in urls {
            var directory: ObjCBool = false
            guard FileManager.default.fileExists(atPath: url.path, isDirectory: &directory) else { continue }
            if directory.boolValue {
                guard let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.isRegularFileKey], options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { continue }
                for case let file as URL in enumerator where extensions.contains(file.pathExtension.lowercased()) {
                    if (try? file.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true { found.insert(file.standardizedFileURL.resolvingSymlinksInPath().path) }
                }
            } else if extensions.contains(url.pathExtension.lowercased()) { found.insert(url.standardizedFileURL.resolvingSymlinksInPath().path) }
        }
        return found.sorted().map { URL(fileURLWithPath: $0) }
    }
    public static func scan(_ urls: [URL], fallbackThumbnail: (@Sendable (URL, URL) -> Bool)? = nil) async throws -> [MediaItem] {
        var result: [MediaItem] = []
        for file in files(in: urls) {
            try Task.checkCancellation()
            var item = parseName(file.deletingPathExtension().lastPathComponent); item.path = file.path
            let asset = AVURLAsset(url: file)
            if let duration = try? await asset.load(.duration), duration.seconds.isFinite { item.duration = max(0, duration.seconds) }
            if let metadata = try? await asset.load(.commonMetadata) {
                for entry in metadata where entry.commonKey == .commonKeyTitle {
                    if let title = try? await entry.load(.stringValue), !title.isEmpty { item.title = title; break }
                }
            }
            let folder = file.deletingLastPathComponent()
            let base = file.deletingPathExtension()
            let nfo = [base.appendingPathExtension("nfo"), folder.appendingPathComponent("movie.nfo"), folder.appendingPathComponent("tvshow.nfo")].first { FileManager.default.fileExists(atPath: $0.path) }
            if let nfo, let data = try? Data(contentsOf: nfo), data.count < 2_000_000 {
                let reader = NFOReader(); let parser = XMLParser(data: data); parser.shouldResolveExternalEntities = false; parser.delegate = reader
                if parser.parse() {
                    if let s = reader.values["title"], !s.isEmpty { item.title = s }
                    item.year = reader.values["year"].flatMap(Int.init) ?? item.year
                    item.season = reader.values["season"].flatMap(Int.init) ?? item.season
                    item.episode = reader.values["episode"].flatMap(Int.init) ?? item.episode
                    item.summary = reader.values["plot"] ?? ""
                }
            }
            let images = [base.appendingPathExtension("jpg"), base.appendingPathExtension("png"), folder.appendingPathComponent("poster.jpg"), folder.appendingPathComponent("poster.png"), folder.appendingPathComponent("folder.jpg")]
            item.artwork = images.first { FileManager.default.fileExists(atPath: $0.path) }?.path
            if item.artwork == nil {
                item.artwork = await thumbnail(asset: asset, path: file.path, duration: item.duration)
                if item.artwork == nil, let fallbackThumbnail {
                    let target = thumbnailURL(path: file.path)
                    if FileManager.default.fileExists(atPath: target.path) || fallbackThumbnail(file, target) { item.artwork = target.path }
                }
            }
            result.append(item)
        }
        return result
    }
    public static func thumbnailURL(path: String) -> URL {
        // Stable filename, never Swift's per-process randomized hash.
        var hash: UInt64 = 14695981039346656037
        for byte in path.utf8 { hash = (hash ^ UInt64(byte)) &* 1099511628211 }
        return Paths.support.appendingPathComponent("thumb-\(hash).jpg")
    }
    private static func thumbnail(asset: AVURLAsset, path: String, duration: Double) async -> String? {
        guard duration > 0 else { return nil }
        let target = thumbnailURL(path: path)
        if FileManager.default.fileExists(atPath: target.path) { return target.path }
        let generator = AVAssetImageGenerator(asset: asset); generator.maximumSize = CGSize(width: 480, height: 480); generator.appliesPreferredTrackTransform = true
        guard let frame = try? await generator.image(at: CMTime(seconds: min(duration * 0.1, 30), preferredTimescale: 600)) else { return nil }
        let bitmap = NSBitmapImageRep(cgImage: frame.image)
        guard let data = bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.8]) else { return nil }
        do { try data.write(to: target, options: .atomic); return target.path } catch { return nil }
    }
}
private final class NFOReader: NSObject, XMLParserDelegate {
    var values: [String: String] = [:]
    var current = "", text = ""
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) { current = elementName; text = "" }
    func parser(_ parser: XMLParser, foundCharacters string: String) { text += string }
    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        if ["title", "year", "season", "episode", "plot"].contains(elementName) { values[elementName] = text.trimmingCharacters(in: .whitespacesAndNewlines) }; text = ""
    }
}
