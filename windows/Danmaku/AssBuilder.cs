using System.Globalization;
using System.IO;
using System.Text;
using DanmakuPlayerWin.Playback;

namespace DanmakuPlayerWin.Danmaku;

public sealed record AssBuildResult(string Text, int Dropped, int Filtered);

/// <summary>
/// 把弹幕轨道生成 ASS 字幕，交给 libmpv/libass 叠加在画面上。
/// 弹幕样式对齐 macOS 版：系统字体加粗、8% 字号的描边、透明度、显示区域、同屏上限、屏蔽词。
/// 轨道分配仍是启发式，不追求像素级避让。
/// </summary>
public static class AssBuilder
{
    public const int PlayResX = 1920;
    public const int PlayResY = 1080;

    /// <summary>顶部/底部固定弹幕的停留时间（mac: 固定 4 秒）。</summary>
    private const double StaticSeconds = 4.0;

    /// <summary>同轨前后两条之间的横向空隙（mac: width + 24）。</summary>
    private const double LaneGapPixels = 24;

    /// <summary>B 站字号（默认 25、小号 18）到 1080p PlayRes 的换算：B 站播放器按 720p 排版，放大 1.5 倍。</summary>
    private const double FontScale = 1.5;

    public static AssBuildResult Build(
        DanmakuTrack track,
        DanmakuSettings settings,
        Func<double, double>? mapBiliToLocal = null,
        double mediaDuration = double.MaxValue)
    {
        var baseFont = Math.Clamp(settings.FontSize * FontScale, 12, 200);
        var lineHeight = baseFont + 8;
        var laneCount = Math.Max(3, (int)(PlayResY * Math.Clamp(settings.Area, 0.25, 1.0) / lineHeight));
        var scrollDuration = settings.ScrollingDuration;
        var perSecond = Math.Max(1, settings.MaxVisible / (int)Math.Ceiling(scrollDuration));
        var alpha = (byte)Math.Round((1 - Math.Clamp(settings.Opacity, 0, 1)) * 255);
        var blocked = settings.BlockedList;

        var lanes = new LaneAllocator(laneCount);
        var bucketCounts = new Dictionary<int, int>();
        var dropped = 0;
        var filtered = 0;
        var sb = new StringBuilder();

        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {PlayResX}");
        sb.AppendLine($"PlayResY: {PlayResY}");
        sb.AppendLine("WrapStyle: 2");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine("Collisions: Normal");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Style: Danmaku,{settings.FontName},{baseFont:0},&H00FFFFFF,&H00FFFFFF,&H00000000,&H80000000,{(settings.Bold ? -1 : 0)},0,0,0,100,100,0,0,1,0,0,7,0,0,0,1"));
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var item in track.Items)
        {
            var localTime = mapBiliToLocal?.Invoke(item.Time) ?? item.Time;
            if (localTime < 0 || localTime > mediaDuration)
            {
                dropped++;
                continue;
            }

            if (!settings.Allows(item.Mode))
            {
                filtered++;
                continue;
            }

            if (blocked.Any(word => item.Text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                filtered++;
                continue;
            }

            // 和 mac 一样的每秒密度上限：同一秒里最多放 maxVisible / 滚动时长的条数
            var bucket = (int)Math.Floor(localTime);
            bucketCounts.TryGetValue(bucket, out var used);
            if (used >= perSecond)
            {
                dropped++;
                continue;
            }

            var fontSize = Math.Clamp(item.FontSize * FontScale, 12, 200);
            var width = EstimateWidth(item.Text, fontSize);
            var colour = ToAssColour(item.Color);
            // 描边加粗到 10% 并加 1px 阴影：亮背景和暗背景上都能看清
            var border = Math.Max(2.5, fontSize * 0.10);

            if (item.Mode is 4 or 5)
            {
                var slot = item.Mode == 5
                    ? lanes.TryReserveTop(localTime, StaticSeconds)
                    : lanes.TryReserveBottom(localTime, StaticSeconds);
                if (slot is not int row)
                {
                    dropped++;
                    continue;
                }

                var y = row * lineHeight;
                var x = (PlayResX - width) / 2;
                bucketCounts[bucket] = used + 1;
                sb.AppendLine(Dialogue(localTime, StaticSeconds,
                    $"{{\\an7\\pos({x:0},{y:0})\\c{colour}\\fs{fontSize:0}\\bord{border:0.0}\\shad1\\4c&H000000&\\alpha&H{alpha:X2}&}}{Escape(item.Text)}"));
                continue;
            }

            var speed = (PlayResX + width) / scrollDuration;
            var scrollSlot = lanes.TryReserveScroll(localTime, width, speed, scrollDuration, PlayResX, LaneGapPixels);
            if (scrollSlot is not int scrollRow)
            {
                dropped++;
                continue;
            }

            var scrollY = scrollRow * lineHeight;
            bucketCounts[bucket] = used + 1;
            var motion = item.Mode == 6
                ? $"\\move({-width:0},{scrollY:0},{PlayResX},{scrollY:0})"
                : $"\\move({PlayResX},{scrollY:0},{-width:0},{scrollY:0})";
            sb.AppendLine(Dialogue(localTime, scrollDuration,
                $"{{\\an7{motion}\\c{colour}\\fs{fontSize:0}\\bord{border:0.0}\\shad1\\4c&H000000&\\alpha&H{alpha:X2}&}}{Escape(item.Text)}"));
        }

        return new AssBuildResult(sb.ToString(), dropped, filtered);
    }

    public static AssBuildResult WriteToFile(
        DanmakuTrack track,
        string path,
        DanmakuSettings settings,
        Func<double, double>? mapBiliToLocal = null,
        double mediaDuration = double.MaxValue)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var result = Build(track, settings, mapBiliToLocal, mediaDuration);
        File.WriteAllText(path, result.Text, new UTF8Encoding(false));
        return result;
    }

    /// <summary>按 XML 路径生成稳定的 ASS 缓存路径（GCHashCode 跨进程不稳定，会破坏 sub-reload）。</summary>
    public static string AssPathFor(string danmakuFile)
    {
        const ulong offset = 1469598103934665603;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var ch in danmakuFile)
        {
            hash ^= ch;
            hash *= prime;
        }
        var dir = Path.Combine(Path.GetTempPath(), "DanmakuPlayer");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"danmaku-{hash:X16}.ass");
    }

    /// <summary>按字符宽度粗估文本像素宽度：CJK 全宽，ASCII 约 0.55 宽。</summary>
    public static double EstimateWidth(string text, double fontSize)
    {
        var units = 0.0;
        foreach (var ch in text)
        {
            units += ch >= 0x2E80 ? 1.0 : (char.IsWhiteSpace(ch) ? 0.3 : 0.55);
        }
        return Math.Max(fontSize, units * fontSize);
    }

    private static string Dialogue(double start, double duration, string text)
    {
        return $"Dialogue: 0,{Timestamp(start)},{Timestamp(start + duration)},Danmaku,,0,0,0,,{text}";
    }

    private static string Timestamp(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 10:00}");
    }

    private static string ToAssColour(int rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return $"&H00{b:X2}{g:X2}{r:X2}&";
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("{", "｛").Replace("}", "｝");

    /// <summary>
    /// 轨道池：顶部固定弹幕占最上面几条、底部固定占最下面几条，滚动只用中间。
    /// 三种类型不共用行——共用会让顶部固定弹幕压到还在屏上滚的弹幕上。
    /// 滚动轨道额外记录上一条的宽度与速度，避免后进的长弹幕追上前面的短弹幕。
    /// </summary>
    private sealed class LaneAllocator
    {
        private sealed record ScrollItem(double Time, double Width, double Speed);

        private readonly double[] _freeAt;
        private readonly ScrollItem?[] _lastScroll;
        private readonly int _topBand;
        private readonly int _bottomBand;

        public LaneAllocator(int lanes)
        {
            _freeAt = new double[lanes];
            _lastScroll = new ScrollItem?[lanes];
            for (var i = 0; i < lanes; i++) _freeAt[i] = double.NegativeInfinity;

            // 至少给顶部/底部各留一行，剩下的给滚动；轨道多时固定弹幕可以叠几层
            _topBand = Math.Max(1, lanes / 5);
            _bottomBand = Math.Max(1, lanes / 5);
            if (lanes - _topBand - _bottomBand < 1)
            {
                _topBand = 1;
                _bottomBand = 1;
            }
        }

        public int? TryReserveTop(double time, double occupancy)
        {
            for (var i = 0; i < _topBand; i++)
            {
                if (_freeAt[i] <= time)
                {
                    _freeAt[i] = time + occupancy;
                    return i;
                }
            }
            return null;
        }

        public int? TryReserveBottom(double time, double occupancy)
        {
            for (var i = _freeAt.Length - 1; i >= _freeAt.Length - _bottomBand; i--)
            {
                if (_freeAt[i] <= time)
                {
                    _freeAt[i] = time + occupancy;
                    return i;
                }
            }
            return null;
        }

        /// <summary>
        /// 滚动弹幕只在中间那一段。同一轨道要同时满足：
        /// 1) 上一条的尾巴已经离开右边界（留出 gap）；
        /// 2) 按本条的移动速度，追上去之前上一条已经快走完——否则长句会压住前面的短句。
        /// </summary>
        public int? TryReserveScroll(double time, double width, double speed, double duration, double playResX, double gap)
        {
            var first = _topBand;
            var last = _freeAt.Length - _bottomBand - 1;
            for (var i = first; i <= last; i++)
            {
                if (_lastScroll[i] is not { } previous)
                {
                    _lastScroll[i] = new ScrollItem(time, width, speed);
                    return i;
                }

                var delta = time - previous.Time;
                var tailClear = (previous.Width + gap) / previous.Speed;
                var noCatchUp = duration - (playResX - gap) / speed;
                if (delta >= tailClear && delta >= noCatchUp)
                {
                    _lastScroll[i] = new ScrollItem(time, width, speed);
                    return i;
                }
            }
            return null;
        }
    }
}
