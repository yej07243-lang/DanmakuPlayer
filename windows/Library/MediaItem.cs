using System.IO;
using System.Text.Json.Serialization;

namespace DanmakuPlayerWin.Library;

/// <summary>媒体库条目，字段对齐 macOS 版 <c>MediaItem</c>（少了 calibration，校准单独按路径存）。</summary>
public sealed class MediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public double Duration { get; set; }
    public double Position { get; set; }
    public bool Watched { get; set; }
    public string? Artwork { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool Available { get; set; } = true;
    public string? Bvid { get; set; }
    public long? Cid { get; set; }
    public string? LocalDanmaku { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string EpisodeLabel => Season is int season && Episode is int episode
        ? $"S{season:00}E{episode:00}"
        : Episode is int single ? $"E{single:00}" : string.Empty;

    /// <summary>「剧集」分栏按片名分组，组头就是这个值。</summary>
    [JsonIgnore]
    public string SeriesGroup => Title;

    [JsonIgnore]
    public string SubtitleLine
    {
        get
        {
            var parts = new List<string>();
            if (Year is int year) parts.Add(year.ToString());
            if (EpisodeLabel.Length > 0) parts.Add(EpisodeLabel);
            if (Duration > 0) parts.Add(FormatTime(Duration));
            if (!Available) parts.Add("文件不可用");
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public string ProgressLine => Duration <= 0
        ? string.Empty
        : Watched
            ? "已看完"
            : Position > 0
                ? $"{FormatTime(Position)} / {FormatTime(Duration)}"
                : string.Empty;

    /// <summary>海报卡片底部的进度条；已看完或没有进度时不显示。</summary>
    [JsonIgnore]
    public double ProgressPercent => Duration > 0 && Position > 0
        ? Math.Clamp(Position / Duration * 100, 0, 100)
        : 0;

    [JsonIgnore]
    public bool HasProgress => !Watched && Duration > 0 && Position > 0;

    /// <summary>卡片上的弹幕角标：有关联来源或同名 XML 边车就算已关联。</summary>
    [JsonIgnore]
    public bool HasDanmaku => !string.IsNullOrWhiteSpace(Bvid) ||
                              (!string.IsNullOrEmpty(Path) && File.Exists(System.IO.Path.ChangeExtension(Path, ".xml")));

    /// <summary>不足一小时用 m:ss，超过用 h:mm:ss。</summary>
    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "0:00";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    public MediaItem Clone() => (MediaItem)MemberwiseClone();
}

public static class MediaExtensions
{
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "mov", "avi", "webm", "m4v", "ts", "m2ts", "flv", "wmv", "mpg", "mpeg"
    };

    public static bool IsVideo(string path) => Video.Contains(System.IO.Path.GetExtension(path).TrimStart('.'));
}
