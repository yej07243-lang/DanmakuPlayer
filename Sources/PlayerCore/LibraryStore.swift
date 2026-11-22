import Foundation
import CSQLite

public final class LibraryStore: @unchecked Sendable {
    private var db: OpaquePointer?
    private let lock = NSRecursiveLock()
    private let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
    public init(url: URL = Paths.support.appendingPathComponent("library.sqlite")) throws {
        guard sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nil) == SQLITE_OK else { throw PlayerError.message("无法打开媒体库数据库") }
        sqlite3_busy_timeout(db, 5000)
        try execute("PRAGMA journal_mode=WAL;")
        try execute("CREATE TABLE IF NOT EXISTS media (id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE, payload BLOB NOT NULL); CREATE TABLE IF NOT EXISTS roots (path TEXT PRIMARY KEY); PRAGMA user_version=1;")
    }
    deinit { sqlite3_close(db) }
    private func execute(_ sql: String) throws {
        guard sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else { throw failure() }
    }
    private func failure() -> PlayerError { .message("媒体库写入失败：\(String(cString: sqlite3_errmsg(db)))") }
    private func prepare(_ sql: String) throws -> OpaquePointer {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let statement else { throw failure() }
        return statement
    }
    public func items() throws -> [MediaItem] {
        lock.lock(); defer { lock.unlock() }
        let s = try prepare("SELECT payload FROM media ORDER BY path"); defer { sqlite3_finalize(s) }
        var result: [MediaItem] = []
        while sqlite3_step(s) == SQLITE_ROW {
            guard let bytes = sqlite3_column_blob(s, 0) else { throw PlayerError.message("媒体库记录为空") }
            let data = Data(bytes: bytes, count: Int(sqlite3_column_bytes(s, 0)))
            result.append(try JSONDecoder().decode(MediaItem.self, from: data))
        }
        return result
    }
    public func save(_ item: MediaItem) throws {
        lock.lock(); defer { lock.unlock() }
        let s = try prepare("INSERT INTO media(id,path,payload) VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET path=excluded.path,payload=excluded.payload"); defer { sqlite3_finalize(s) }
        sqlite3_bind_text(s, 1, item.id, -1, transient); sqlite3_bind_text(s, 2, item.path, -1, transient)
        let data = try JSONEncoder().encode(item)
        _ = data.withUnsafeBytes { sqlite3_bind_blob(s, 3, $0.baseAddress, Int32(data.count), transient) }
        guard sqlite3_step(s) == SQLITE_DONE else { throw failure() }
    }
    public func merge(_ scanned: [MediaItem]) throws {
        lock.lock(); defer { lock.unlock() }
        let existing = Dictionary(uniqueKeysWithValues: try items().map { ($0.path, $0) })
        try execute("BEGIN IMMEDIATE")
        do {
            for candidate in scanned {
                var item = existing[candidate.path] ?? candidate
                item.available = true
                if item.duration == 0 { item.duration = candidate.duration }
                if item.artwork == nil { item.artwork = candidate.artwork }
                try save(item)
            }
            try execute("COMMIT")
        } catch { try? execute("ROLLBACK"); throw error }
    }
    public func remove(id: String) throws {
        lock.lock(); defer { lock.unlock() }
        let s = try prepare("DELETE FROM media WHERE id=?"); defer { sqlite3_finalize(s) }
        sqlite3_bind_text(s, 1, id, -1, transient)
        guard sqlite3_step(s) == SQLITE_DONE else { throw failure() }
    }
    public func addRoot(_ path: String) throws {
        lock.lock(); defer { lock.unlock() }
        let s = try prepare("INSERT OR IGNORE INTO roots(path) VALUES(?)"); defer { sqlite3_finalize(s) }
        sqlite3_bind_text(s, 1, path, -1, transient)
        guard sqlite3_step(s) == SQLITE_DONE else { throw failure() }
    }
    public func roots() throws -> [String] {
        lock.lock(); defer { lock.unlock() }
        let s = try prepare("SELECT path FROM roots ORDER BY path"); defer { sqlite3_finalize(s) }
        var result: [String] = []
        while sqlite3_step(s) == SQLITE_ROW { result.append(String(cString: sqlite3_column_text(s, 0))) }
        return result
    }
}
