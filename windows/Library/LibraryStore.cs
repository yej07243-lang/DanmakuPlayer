using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DanmakuPlayerWin.Library;

/// <summary>
/// 媒体库存储，结构照搬 macOS 版：media(id, path, payload) + roots(path)，
/// payload 是 MediaItem 的 JSON，schema 升级只要改 JSON 不用动表。
/// </summary>
public sealed class LibraryStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public LibraryStore(string? databasePath = null)
    {
        var path = databasePath ?? System.IO.Path.Combine(DefaultDirectory, "library.sqlite");
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS media (id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS roots (path TEXT PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
        DatabasePath = path;
    }

    public string DatabasePath { get; }

    public static string DefaultDirectory => AppPaths.DataDirectory;

    public IReadOnlyList<MediaItem> Items()
    {
        lock (_lock)
        {
            var result = new List<MediaItem>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM media ORDER BY path";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var payload = (byte[])reader[0];
                var item = JsonSerializer.Deserialize<MediaItem>(payload, JsonOptions);
                if (item != null) result.Add(item);
            }
            return result;
        }
    }

    public void Save(MediaItem item)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO media(id,path,payload) VALUES($id,$path,$payload) " +
                                  "ON CONFLICT(id) DO UPDATE SET path=excluded.path, payload=excluded.payload";
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$path", item.Path);
            command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>扫描结果合并进库：按路径保留已有进度/标题/来源，只补缺失字段。</summary>
    public void Merge(IEnumerable<MediaItem> scanned)
    {
        lock (_lock)
        {
            var existing = Items().ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var candidate in scanned)
                {
                    var item = existing.TryGetValue(candidate.Path, out var found) ? found : candidate;
                    item.Available = true;
                    if (item.Duration <= 0) item.Duration = candidate.Duration;
                    if (string.IsNullOrWhiteSpace(item.Title)) item.Title = candidate.Title;
                    if (item.Artwork == null) item.Artwork = candidate.Artwork;
                    if (string.IsNullOrWhiteSpace(item.Summary)) item.Summary = candidate.Summary;
                    item.Year ??= candidate.Year;
                    item.Season ??= candidate.Season;
                    item.Episode ??= candidate.Episode;
                    item.Modified = DateTime.UtcNow;
                    Save(item);
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public MediaItem? FindByPath(string path)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM media WHERE path=$path";
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return JsonSerializer.Deserialize<MediaItem>((byte[])reader[0], JsonOptions);
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM media WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void AddRoot(string path)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO roots(path) VALUES($path)";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<string> Roots()
    {
        lock (_lock)
        {
            var result = new List<string>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path FROM roots ORDER BY path";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    public void Dispose() => _connection.Dispose();
}
