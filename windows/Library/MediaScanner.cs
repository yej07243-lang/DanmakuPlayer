using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DanmakuPlayerWin.Library;

/// <summary>
/// 扫描本地视频并把文件名/NFO/海报解析成媒体库条目。
/// 对齐 macOS 版 MediaScanner：同样的扩展名集合、S01E01 与年份解析、NFO 字段、同目录海报，
/// 缩略图用 ffmpeg 抽第 min(时长*10%, 30) 秒的一帧。
/// </summary>
public static class MediaScanner
{
    private static readonly Regex EpisodePattern = new(@"(?i)[ ._-]*s(\d{1,2})e(\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex YearPattern = new(@"(?:^|[ ._(\[])(19\d{2}|20\d{2})(?=$|[ ._)\]])", RegexOptions.Compiled);

    public static string ThumbnailDirectory => AppPaths.ThumbnailDirectory;

    public static MediaItem ParseName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var item = new MediaItem { Path = string.Empty, Title = name };

        var episode = EpisodePattern.Match(name);
        if (episode.Success)
        {
            item.Season = int.TryParse(episode.Groups[1].Value, out var season) ? season : null;
            item.Episode = int.TryParse(episode.Groups[2].Value, out var number) ? number : null;
            item.Title = name[..episode.Index];
        }

        var year = YearPattern.Match(item.Title);
        if (year.Success)
        {
            if (int.TryParse(year.Groups[1].Value, out var parsed)) item.Year = parsed;
            item.Title = item.Title[..year.Index];
        }

        item.Title = item.Title.Replace('.', ' ').Replace('_', ' ').Trim();
        if (item.Title.Length == 0) item.Title = name;
        return item;
    }

    public static IReadOnlyList<string> Files(IEnumerable<string> inputs)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                if (MediaExtensions.IsVideo(input)) found.Add(NormalizePath(input));
                continue;
            }

            if (!Directory.Exists(input)) continue;
            try
            {
                CollectFiles(input, found);
            }
            catch (UnauthorizedAccessException)
            {
                // 没权限的子目录跳过，不影响整体扫描
            }
        }

        return found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>递归收集视频；跳过隐藏/系统文件和目录、符号链接目录和回收站。</summary>
    private static void CollectFiles(string directory, ISet<string> found)
    {
        var info = new DirectoryInfo(directory);
        foreach (var file in info.EnumerateFiles())
        {
            if (IsHiddenOrSystem(file)) continue;
            if (!MediaExtensions.IsVideo(file.FullName)) continue;
            found.Add(NormalizePath(file.FullName));
        }

        foreach (var child in info.EnumerateDirectories())
        {
            if (IsHiddenOrSystem(child)) continue;
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            try
            {
                CollectFiles(child.FullName, found);
            }
            catch (UnauthorizedAccessException)
            {
                // 单个子目录没权限就跳过
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static bool IsHiddenOrSystem(FileSystemInfo info)
    {
        try
        {
            info.Refresh();
            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
            if (info.Name.StartsWith('.')) return true;

            var name = info.Name;
            return name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析符号链接后返回真实路径，避免同一文件通过两条路径重复入库。</summary>
    private static string NormalizePath(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is { Exists: true }) return Path.GetFullPath(target.FullName);
        }
        catch
        {
            // 解析链接失败就用原路径
        }

        return Path.GetFullPath(path);
    }

    public static List<MediaItem> Scan(IEnumerable<string> inputs, IProgress<string>? progress = null)
    {
        var result = new List<MediaItem>();
        var files = Files(inputs);

        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            progress?.Report($"({i + 1}/{files.Count}) {Path.GetFileName(path)}");

            try
            {
                var item = ParseName(Path.GetFileName(path));
                item.Path = path;
                var probe = ProbeInfo(path);
                item.Duration = probe.Duration;
                if (!string.IsNullOrWhiteSpace(probe.Title)) item.Title = probe.Title.Trim();
                ApplyNfo(item);
                item.Artwork = FindArtwork(path) ?? CreateThumbnail(path, item.Duration);
                result.Add(item);
            }
            catch (Exception ex)
            {
                progress?.Report($"跳过 {Path.GetFileName(path)}：{ex.Message}");
            }
        }

        return result;
    }

    private static void ApplyNfo(MediaItem item)
    {
        var directory = Path.GetDirectoryName(item.Path) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(item.Path);
        var candidates = new[]
        {
            Path.Combine(directory, baseName + ".nfo"),
            Path.Combine(directory, "movie.nfo"),
            Path.Combine(directory, "tvshow.nfo")
        };

        var nfo = candidates.FirstOrDefault(File.Exists);
        if (nfo == null) return;

        try
        {
            if (new FileInfo(nfo).Length > 2_000_000) return;
            var doc = XDocument.Load(nfo);
            string? Value(string name) => doc.Descendants(name).FirstOrDefault()?.Value?.Trim();

            var title = Value("title");
            if (!string.IsNullOrWhiteSpace(title)) item.Title = title;
            if (int.TryParse(Value("year"), out var year)) item.Year = year;
            if (int.TryParse(Value("season"), out var season)) item.Season = season;
            if (int.TryParse(Value("episode"), out var episode)) item.Episode = episode;
            var plot = Value("plot");
            if (!string.IsNullOrWhiteSpace(plot)) item.Summary = plot;
        }
        catch
        {
            // NFO 坏了就当没有
        }
    }

    private static string? FindArtwork(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(path);
        var candidates = new[]
        {
            Path.Combine(directory, baseName + ".jpg"),
            Path.Combine(directory, baseName + ".png"),
            Path.Combine(directory, "poster.jpg"),
            Path.Combine(directory, "poster.png"),
            Path.Combine(directory, "folder.jpg")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? CreateThumbnail(string path, double duration)
    {
        if (duration <= 0) return null;

        Directory.CreateDirectory(ThumbnailDirectory);
        var target = Path.Combine(ThumbnailDirectory, $"thumb-{StableHash(path):X16}.jpg");
        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

        var seek = Math.Min(duration * 0.1, 30);
        var args = new List<string>
        {
            "-v", "error",
            "-ss", seek.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path,
            "-frames:v", "1",
            "-vf", "scale=480:-2",
            "-q:v", "5",
            "-y", target
        };

        if (RunTool("ffmpeg", args) == null) return null;
        return File.Exists(target) && new FileInfo(target).Length > 0 ? target : null;
    }

    /// <summary>一次 ffprobe 同时拿片长和容器内置标题（对齐 mac 版 AVFoundation 的元数据导入）。</summary>
    private static (double Duration, string? Title) ProbeInfo(string path)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-show_entries", "format=duration:format_tags=title",
            "-of", "default=noprint_wrappers=1",
            path
        };

        var output = RunTool("ffprobe", args);
        var duration = 0.0;
        string? title = null;
        if (!string.IsNullOrWhiteSpace(output))
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("duration=", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(line["duration=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    duration = parsed;
                }
                else if (line.StartsWith("TAG:title=", StringComparison.OrdinalIgnoreCase))
                {
                    title = line["TAG:title=".Length..].Trim();
                }
            }
        }

        // 发布包只带 libmpv；干净机器没有 ffprobe 时用无窗口 libmpv 兜底
        if (duration <= 0)
        {
            var probe = DanmakuPlayerWin.Mpv.MpvProbe.Probe(path);
            duration = probe.Duration;
            if (string.IsNullOrWhiteSpace(title)) title = probe.Title;
        }

        if (!string.IsNullOrWhiteSpace(title) &&
            (string.Equals(title.Trim(), Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(title.Trim(), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
        {
            title = null; // 媒体标题就是文件名时不覆盖文件名解析结果
        }

        return (duration, title);
    }

    /// <summary>跑外部工具并拿到合并后的输出；失败返回 null。</summary>
    private static string? RunTool(string tool, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static ulong StableHash(string text)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var ch in text) hash = (hash ^ ch) * prime;
        return hash;
    }
}
