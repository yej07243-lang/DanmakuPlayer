using System.IO;
using System.Text.Json;

namespace DanmakuPlayerWin.Playback;

/// <summary>
/// 按文件路径记住上次播放位置和「已看完」状态，走 JSON 文件，不碰 mac 版 SQLite。
/// 对齐 mac 版：播放中约每 5 秒保存一次；进度超过 95% 标记已看完并清掉续播位置；
/// 已看完的条目再次播放时从头开始。
/// </summary>
public sealed class ResumeStore
{
    private sealed record Entry(double Position, string UpdatedUtc, bool Watched = false);

    private readonly string _path;
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ResumeStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "resume.json");
        Load();
    }

    public string StorePath => _path;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json);
            if (data != null) _entries = new Dictionary<string, Entry>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public double? GetPosition(string file)
    {
        return _entries.TryGetValue(Normalize(file), out var entry) ? entry.Position : null;
    }

    public bool IsWatched(string file) =>
        _entries.TryGetValue(Normalize(file), out var entry) && entry.Watched;

    /// <summary>上次保存时间，媒体库详情用它显示「继续播放 · 保存于 …」。</summary>
    public DateTime? GetSavedAt(string file)
    {
        if (!_entries.TryGetValue(Normalize(file), out var entry)) return null;
        return DateTime.TryParse(entry.UpdatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at.ToLocalTime()
            : null;
    }

    public void Save(string file, double position)
    {
        var key = Normalize(file);
        _entries[key] = new Entry(Math.Max(0, position), DateTime.UtcNow.ToString("o"), Watched: false);
        Persist();
    }

    /// <summary>进度过 95%：标记已看完，同时把续播位置清零。</summary>
    public void MarkWatched(string file)
    {
        _entries[Normalize(file)] = new Entry(0, DateTime.UtcNow.ToString("o"), Watched: true);
        Persist();
    }

    /// <summary>手动取消「已看完」，保留当前位置字段（用户可能又从头看了一小段）。</summary>
    public void MarkUnwatched(string file)
    {
        var key = Normalize(file);
        if (!_entries.TryGetValue(key, out var entry)) return;
        _entries[key] = entry with { Watched = false, UpdatedUtc = DateTime.UtcNow.ToString("o") };
        Persist();
    }

    public void Clear(string file)
    {
        if (_entries.Remove(Normalize(file))) Persist();
    }

    /// <summary>文件重新定位后把进度和已看完状态搬到新路径。</summary>
    public void Move(string oldFile, string newFile)
    {
        var oldKey = Normalize(oldFile);
        var newKey = Normalize(newFile);
        if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return;
        if (!_entries.TryGetValue(oldKey, out var entry)) return;

        _entries[newKey] = entry;
        _entries.Remove(oldKey);
        Persist();
    }

    private void Persist()
    {
        if (_entries.Count > 500)
        {
            var stale = _entries.OrderBy(e => e.Value.UpdatedUtc, StringComparer.Ordinal)
                .Take(_entries.Count - 500).Select(e => e.Key).ToList();
            foreach (var k in stale) _entries.Remove(k);
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 记住位置失败不应该打断播放
        }
    }

    private static string Normalize(string file) => Path.GetFullPath(file);
}
