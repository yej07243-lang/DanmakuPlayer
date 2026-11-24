using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DanmakuPlayerWin.Playback;

/// <summary>一个校准锚点：本地播放到 <paramref name="Local"/> 秒时，对应 B 站时间轴的第 <paramref name="Bili"/> 秒。</summary>
public readonly record struct Anchor(double Local, double Bili);

/// <summary>给界面用的锚点快照。</summary>
public readonly record struct CalibrationAnchorInfo(double Local, double Bili);

/// <summary>
/// 分段校准：把 B 站时间轴按锚点切成若干段，段内按 1:1 平移映射到本地时间轴。
/// 两版片长不同通常是剪片造成的阶跃差异，不是变速，所以每段用固定偏移而不是线性缩放。
/// </summary>
public sealed class Calibration
{
    private readonly List<Anchor> _anchors = new();

    public Calibration(string? danmakuSource = null, IEnumerable<Anchor>? anchors = null)
    {
        DanmakuSource = danmakuSource;
        if (anchors != null) _anchors.AddRange(anchors);
        Normalize();
    }

    public string? DanmakuSource { get; set; }

    /// <summary>整体延后（秒）：正数让弹幕更晚出现，和播放器的 sub-delay 同向。</summary>
    public double OffsetSeconds { get; set; }

    public IReadOnlyList<Anchor> Anchors => _anchors;

    public bool IsEmpty => _anchors.Count == 0;

    public void Add(Anchor anchor)
    {
        if (double.IsNaN(anchor.Local) || double.IsNaN(anchor.Bili)) return;
        _anchors.Add(anchor);
        Normalize();
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _anchors.Count) return false;
        _anchors.RemoveAt(index);
        return true;
    }

    public void Clear() => _anchors.Clear();

    private void Normalize() => _anchors.Sort((a, b) => a.Bili.CompareTo(b.Bili));

    /// <summary>B 站时间 → 本地时间。无锚点或锚点无效时原样返回。</summary>
    public double ToLocal(double biliSeconds)
    {
        if (_anchors.Count == 0) return biliSeconds;

        var anchor = _anchors[0];
        foreach (var candidate in _anchors)
        {
            if (candidate.Bili <= biliSeconds) anchor = candidate;
            else break;
        }
        return anchor.Local + (biliSeconds - anchor.Bili);
    }

    /// <summary>本地时间 → B 站时间，打点时用来预填。</summary>
    public double ToBili(double localSeconds)
    {
        if (_anchors.Count == 0) return localSeconds;

        var anchor = _anchors[0];
        foreach (var candidate in _anchors)
        {
            if (candidate.Local <= localSeconds) anchor = candidate;
            else break;
        }
        return anchor.Bili + (localSeconds - anchor.Local);
    }

    /// <summary>当前播放位置所在段的偏移（本地 - B 站），用于界面显示。</summary>
    public double OffsetAt(double localSeconds)
    {
        if (_anchors.Count == 0) return 0;
        var anchor = _anchors[0];
        foreach (var candidate in _anchors)
        {
            if (candidate.Local <= localSeconds) anchor = candidate;
            else break;
        }
        return anchor.Local - anchor.Bili;
    }

    public bool TryValidate(out string? error)
    {
        if (_anchors.Count == 0)
        {
            error = null;
            return true;
        }

        for (var i = 1; i < _anchors.Count; i++)
        {
            if (_anchors[i].Bili <= _anchors[i - 1].Bili)
            {
                error = $"锚点 {i + 1} 的 B 站时间必须大于前一个（{_anchors[i - 1].Bili:0.0}s）";
                return false;
            }
            if (_anchors[i].Local <= _anchors[i - 1].Local)
            {
                error = $"锚点 {i + 1} 的本地时间必须大于前一个（{_anchors[i - 1].Local:0.0}s）";
                return false;
            }
        }

        error = null;
        return true;
    }
}

/// <summary>校准文件读写。按媒体文件绝对路径存一条记录，与 ResumeStore 同目录。</summary>
public static class CalibrationStore
{
    private sealed class AnchorDto
    {
        public double Local { get; set; }
        public double Bili { get; set; }
    }

    private sealed class EntryDto
    {
        public string? DanmakuSource { get; set; }
        public double OffsetSeconds { get; set; }
        public List<AnchorDto> Anchors { get; set; } = new();
        public string UpdatedUtc { get; set; } = string.Empty;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "calibration.json");

    public static Calibration? Load(string mediaPath, string? storePath = null)
    {
        var path = storePath ?? DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<Dictionary<string, EntryDto>>(File.ReadAllText(path));
            if (data == null || !data.TryGetValue(Path.GetFullPath(mediaPath), out var entry)) return null;
            return FromEntry(entry);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string mediaPath, Calibration calibration, string? storePath = null)
    {
        var path = storePath ?? DefaultPath;
        try
        {
            Dictionary<string, EntryDto> data;
            if (File.Exists(path))
            {
                data = JsonSerializer.Deserialize<Dictionary<string, EntryDto>>(File.ReadAllText(path))
                       ?? new Dictionary<string, EntryDto>();
            }
            else
            {
                data = new Dictionary<string, EntryDto>();
            }

            data[Path.GetFullPath(mediaPath)] = new EntryDto
            {
                DanmakuSource = calibration.DanmakuSource,
                OffsetSeconds = calibration.OffsetSeconds,
                Anchors = calibration.Anchors.Select(a => new AnchorDto { Local = a.Local, Bili = a.Bili }).ToList(),
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            };

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 保存失败不该打断播放
        }
    }

    /// <summary>
    /// 从任意 JSON 文件读校准，兼容三种形状：
    /// 1) 以媒体路径为键的存储文件；2) 单条 <c>{ "anchors": [...] }</c>；3) 裸数组。
    /// </summary>
    public static Calibration? LoadFlexible(string file, string? mediaPath)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file);

            if (mediaPath != null)
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, EntryDto>>(text);
                if (map != null && map.TryGetValue(Path.GetFullPath(mediaPath), out var entry)) return FromEntry(entry);
            }

            var single = JsonSerializer.Deserialize<EntryDto>(text);
            if (single?.Anchors.Count > 0) return FromEntry(single);

            var list = JsonSerializer.Deserialize<List<AnchorDto>>(text);
            if (list is { Count: > 0 })
                return new Calibration(null, list.Select(a => new Anchor(a.Local, a.Bili)));

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Calibration FromEntry(EntryDto entry) =>
        new(entry.DanmakuSource, entry.Anchors.Select(a => new Anchor(a.Local, a.Bili)))
        {
            OffsetSeconds = entry.OffsetSeconds
        };

    /// <summary>文件重新定位后把校准（含整体延后）搬到新路径，观看进度沿用原记录。</summary>
    public static void Move(string oldMediaPath, string newMediaPath, string? storePath = null)
    {
        var path = storePath ?? DefaultPath;
        try
        {
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, EntryDto>>(File.ReadAllText(path));
            if (data == null) return;

            var oldKey = Path.GetFullPath(oldMediaPath);
            var newKey = Path.GetFullPath(newMediaPath);
            if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return;
            if (!data.TryGetValue(oldKey, out var entry)) return;

            data[newKey] = entry;
            data.Remove(oldKey);
            Write(path, data);
        }
        catch
        {
            // 迁移失败只影响校准，不应该挡住重新定位
        }
    }

    private static void Write(string path, Dictionary<string, EntryDto> data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>解析 12:34.5 / 1:02:03 / 754.5 这类时间输入。</summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;

        var total = 0.0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return false;
            if (value < 0) return false;
            total = total * 60 + value;
        }

        seconds = total;
        return true;
    }

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "--:--";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 100}"
            : $"{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 100}";
    }
}
