using System.IO;
using System.Text.Json;

namespace DanmakuPlayerWin.Playback;

/// <summary>
/// 弹幕显示设置。字号 25、显示区域 0.65、滚动 8 秒（速度 1.0）沿用 mac 版；
/// 透明度/同屏上限/粗体按 Windows 实机观感调过：全不透明、同屏 40、默认粗体，热门片源不至于糊成一片。
/// </summary>
public sealed class DanmakuSettings
{
    public bool Enabled { get; set; } = true;
    public double FontSize { get; set; } = 25;

    /// <summary>弹幕字体名（Windows 字体族名），默认微软雅黑；B 站播放器可选项另有黑体/宋体/仿宋/等线。</summary>
    public string FontName { get; set; } = "Microsoft YaHei";

    /// <summary>粗体。默认开：mac 版用 semibold，实机上比常规体更容易读清。</summary>
    public bool Bold { get; set; } = true;

    /// <summary>不透明度。默认全不透明，避免和画面混在一起看不清。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>同屏上限。默认收紧到 40，弹幕密集时不至于糊成一片。</summary>
    public int MaxVisible { get; set; } = 40;

    public double Area { get; set; } = 0.65;
    public string BlockedWords { get; set; } = string.Empty;
    public bool Scrolling { get; set; } = true;
    public bool Top { get; set; } = true;
    public bool Bottom { get; set; } = true;
    public double ScrollSpeed { get; set; } = 1.0;

    /// <summary>一条滚动弹幕穿过屏幕的时间；速度越大越短（mac: 8 / speed）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double ScrollingDuration => 8.0 / Math.Clamp(ScrollSpeed, 0.5, 2.0);

    /// <summary>模式 4 = 底部、5 = 顶部，其余都按滚动处理（和 mac 一致，只支持 1–6）。</summary>
    public bool Allows(int mode) => mode == 4 ? Bottom : mode == 5 ? Top : Scrolling;

    public DanmakuSettings Clone() => new()
    {
        Enabled = Enabled,
        FontSize = FontSize,
        FontName = FontName,
        Bold = Bold,
        Opacity = Opacity,
        MaxVisible = MaxVisible,
        Area = Area,
        BlockedWords = BlockedWords,
        Scrolling = Scrolling,
        Top = Top,
        Bottom = Bottom,
        ScrollSpeed = ScrollSpeed
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> BlockedList =>
        BlockedWords.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .ToList();
}

public static class DanmakuSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "danmaku-settings.json");

    public static DanmakuSettings Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return new DanmakuSettings();
            return JsonSerializer.Deserialize<DanmakuSettings>(File.ReadAllText(file)) ?? new DanmakuSettings();
        }
        catch
        {
            return new DanmakuSettings();
        }
    }

    public static void Save(DanmakuSettings settings, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // 设置存不下不该打断播放
        }
    }
}
