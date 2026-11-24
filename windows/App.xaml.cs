using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using DanmakuPlayerWin.Bili;
using DanmakuPlayerWin.Library;
using DanmakuPlayerWin.Playback;

namespace DanmakuPlayerWin;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 少数机器/远程会话的 D3D 会失效但 WPF 不自动回退，界面会整片白。
        // 设置 DANMAKU_SOFTWARE_RENDER=1 可强制软件渲染，截图和自检也用它。
        if (Environment.GetEnvironmentVariable("DANMAKU_SOFTWARE_RENDER") == "1")
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        Theme.ThemeManager.Initialize();

        var args = e.Args;

        if (args.Length >= 1 && args[0].Equals("--check-calibration", StringComparison.OrdinalIgnoreCase))
        {
            RunCalibrationCheck();
            return;
        }

        if (args.Length >= 1 && args[0].Equals("--check-maintenance", StringComparison.OrdinalIgnoreCase))
        {
            RunMaintenanceCheck();
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--scan", StringComparison.OrdinalIgnoreCase))
        {
            RunScan(args.Skip(1).ToArray());
            return;
        }

        if (args.Length >= 1 && args[0].Equals("--library", StringComparison.OrdinalIgnoreCase))
        {
            RunLibraryReport();
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--bili-search", StringComparison.OrdinalIgnoreCase))
        {
            RunBiliSearch(args[1], args.Any(a => a.Equals("--official", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        // 不带参数：进媒体库；带文件：直接开播放器（脚本和双击视频都走这条）
        var libraryMode = args.Length == 0;
        Window window = libraryMode ? new LibraryWindow() : new MainWindow();
        MainWindow = window;
        window.Show();

        if (libraryMode) return;

        if (args.Length >= 2 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {            var file = args[1];
            var report = Path.Combine(AppContext.BaseDirectory, "logs", "selftest-report.json");
            string? danmaku = null;
            string? calibration = null;
            string? lateCalibration = null;
            string? expect = null;
            string? absent = null;
            string? settingsFile = null;
            string? subtitleFile = null;
            string? biliInput = null;
            string? biliCid = null;
            string? biliTitle = null;
            var biliPick = 0;
            var biliRefresh = args.Any(a => a.Equals("--bili-refresh", StringComparison.OrdinalIgnoreCase));
            var offset = 0.0;
            var probeUi = args.Any(a =>
                a.Equals("--probe-ui", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--probe-calibration-ui", StringComparison.OrdinalIgnoreCase));
            var goFullscreen = args.Any(a => a.Equals("--fullscreen", StringComparison.OrdinalIgnoreCase));
            double? at = null;

            for (var i = 2; i < args.Length - 1; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--report":
                        report = args[++i];
                        break;
                    case "--danmaku":
                        danmaku = args[++i];
                        break;
                    case "--calibration":
                        calibration = args[++i];
                        break;
                    case "--late-calibration":
                        lateCalibration = args[++i];
                        break;
                    case "--settings":
                        settingsFile = args[++i];
                        break;
                    case "--subtitle":
                        subtitleFile = args[++i];
                        break;
                    case "--bili":
                        biliInput = args[++i];
                        break;
                    case "--bili-pick":
                        int.TryParse(args[++i], out biliPick);
                        break;
                    case "--expect":
                        expect = args[++i];
                        break;
                    case "--expect-absent":
                        absent = args[++i];
                        break;
                    case "--offset":
                        double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out offset);
                        break;
                    case "--at":
                        if (double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                            at = seconds;
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(biliInput))
            {
                using var client = new BiliClient();
                var targets = client.Resolve(biliInput);
                var target = targets[Math.Clamp(biliPick, 0, targets.Count - 1)];
                danmaku = client.FetchDanmaku(target.Cid, biliRefresh);
                biliCid = target.Cid.ToString();
                biliTitle = target.Title;
            }

            ((MainWindow)window).RunSelfTest(file, report, danmaku, offset, calibration, at, expect, lateCalibration, probeUi, settingsFile, absent, subtitleFile, biliCid, biliTitle, goFullscreen);
        }
        else if (args.Length >= 1 && File.Exists(args[0]))
        {
            ((MainWindow)window).RequestOpen(args[0]);
        }
    }

    /// <summary>命令行搜索：验证 WBI 签名 + 搜索接口，把候选和第一个候选的分集写成 JSON。</summary>
    private void RunBiliSearch(string keyword, bool official)
    {
        var report = new Dictionary<string, object?>();
        try
        {
            using var client = new BiliClient();
            var candidates = client.Search(keyword, official);
            report["ok"] = true;
            report["keyword"] = keyword;
            report["official"] = official;
            report["count"] = candidates.Count;
            report["candidates"] = candidates.Take(10).Select(c => new { c.Bvid, c.SeasonId, c.Title, c.Author, c.Duration }).ToList();
            Diagnostics.Log($"bili-search keyword={keyword} official={official} count={candidates.Count}");

            if (candidates.Count > 0)
            {
                var targets = client.ResolveCandidate(candidates[0]);
                report["firstTargets"] = targets.Take(12).Select(t => new { t.Bvid, t.Cid, t.Title, t.Page, duration = Math.Round(t.DurationSeconds, 1) }).ToList();
            }
        }
        catch (Exception ex)
        {
            report["ok"] = false;
            report["error"] = ex.Message;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "logs", "bili-search.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        if (AttachConsole(-1)) Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(report["ok"] is true ? 0 : 1);
    }

    /// <summary>命令行读取媒体库（含续播位置合并），把分栏计数与条目写成 JSON。</summary>
    private void RunLibraryReport()
    {
        var report = new Dictionary<string, object?>();
        try
        {
            using var store = new LibraryStore();
            var resume = new ResumeStore();
            var items = store.Items().ToList();

            foreach (var item in items)
            {
                item.Available = File.Exists(item.Path);
                if (resume.GetPosition(item.Path) is { } position && position > 0)
                {
                    item.Position = position;
                    item.Watched = item.Duration > 0 && position / item.Duration > 0.95;
                }
            }

            report["ok"] = true;
            report["database"] = store.DatabasePath;
            report["counts"] = new            {
                all = items.Count,
                movies = items.Count(i => i.Episode == null),
                series = items.Count(i => i.Episode != null),
                continueWatching = items.Count(i => i.Position > 0 && !i.Watched),
                unavailable = items.Count(i => !i.Available)
            };
            report["items"] = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Select(i => new
            {
                i.Title,
                episode = i.EpisodeLabel,
                duration = Math.Round(i.Duration, 2),
                position = Math.Round(i.Position, 2),
                i.Watched,
                i.Available
            }).ToList();
        }
        catch (Exception ex)
        {
            report["ok"] = false;
            report["error"] = ex.ToString();
        }

        var path = Path.Combine(AppContext.BaseDirectory, "logs", "library-report.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        if (AttachConsole(-1)) Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(report["ok"] is true ? 0 : 1);
    }

    /// <summary>命令行扫描：把目录导入媒体库并把结果写成 JSON，供自动化核对。</summary>
    private void RunScan(string[] inputs)
    {
        var report = new Dictionary<string, object?>();
        try
        {
            using var store = new LibraryStore();
            foreach (var input in inputs) store.AddRoot(System.IO.Path.GetFullPath(input));

            var scanned = MediaScanner.Scan(inputs);
            store.Merge(scanned);
            var items = store.Items();

            report["ok"] = true;
            report["database"] = store.DatabasePath;
            report["scanned"] = scanned.Count;
            report["stored"] = items.Count;
            Diagnostics.Log($"scan-cli inputs={inputs.Length} scanned={scanned.Count} stored={items.Count}");
            report["roots"] = store.Roots();
            report["items"] = items.Select(i => new
            {
                i.Path,
                i.Title,
                i.Season,
                i.Episode,
                i.Year,
                duration = Math.Round(i.Duration, 2),
                i.Artwork,
                i.Available,
                i.Summary
            }).ToList();
        }
        catch (Exception ex)
        {
            report["ok"] = false;
            report["error"] = ex.ToString();
        }

        var path = Path.Combine(AppContext.BaseDirectory, "logs", "scan-report.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        if (AttachConsole(-1)) Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(report["ok"] is true ? 0 : 1);
    }

    private void RunCalibrationCheck()
    {
        var lines = new List<string>();
        var ok = CalibrationChecks.Run(lines.Add);

        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "calibration-check.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, lines);
        }
        catch
        {
            // 写不了日志也不影响退出码
        }

        if (AttachConsole(-1))
        {
            foreach (var line in lines) Console.WriteLine(line);
        }

        Shutdown(ok ? 0 : 1);
    }

    /// <summary>
    /// 验证重新定位时的数据迁移：观看进度、校准（含整体延后）和弹幕边车都应该跟着新路径走。
    /// 纯文件级测试，不弹界面。
    /// </summary>
    private void RunMaintenanceCheck()
    {
        var lines = new List<string>();
        var ok = true;
        void Check(string name, bool pass)
        {
            ok &= pass;
            lines.Add($"{(pass ? "PASS" : "FAIL")}  {name}");
        }

        var root = Path.Combine(Path.GetTempPath(), "DanmakuPlayer-maintenance-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var oldVideo = Path.Combine(root, "old.mp4");
            File.WriteAllText(oldVideo, "video");

            var movedDirectory = Path.Combine(root, "moved");
            Directory.CreateDirectory(movedDirectory);
            var newVideo = Path.Combine(movedDirectory, "new.mp4");
            File.Move(oldVideo, newVideo);

            File.WriteAllText(Path.ChangeExtension(oldVideo, ".xml"), "<i></i>");
            File.WriteAllText(Path.ChangeExtension(oldVideo, ".bili.json"), "{}");

            var resume = new ResumeStore(Path.Combine(root, "resume.json"));
            resume.Save(oldVideo, 12.5);
            resume.Move(oldVideo, newVideo);
            Check("resume moved to new path", Math.Abs((resume.GetPosition(newVideo) ?? -1) - 12.5) < 0.001);
            Check("resume removed from old path", resume.GetPosition(oldVideo) == null);

            var calibrationPath = Path.Combine(root, "calibration.json");
            CalibrationStore.Save(oldVideo,
                new Calibration("source.xml", new[] { new Anchor(10, 9) }) { OffsetSeconds = 2.5 },
                calibrationPath);
            CalibrationStore.Move(oldVideo, newVideo, calibrationPath);
            var moved = CalibrationStore.Load(newVideo, calibrationPath);
            Check("calibration moved to new path", moved is { Anchors.Count: 1 });
            Check("calibration offset preserved", moved != null && Math.Abs(moved.OffsetSeconds - 2.5) < 0.001);
            Check("calibration removed from old path", CalibrationStore.Load(oldVideo, calibrationPath) == null);

            Relocation.MigrateSidecars(oldVideo, newVideo);
            Check("danmaku sidecar copied", File.Exists(Path.ChangeExtension(newVideo, ".xml")));
            Check("bili source sidecar copied", File.Exists(Path.ChangeExtension(newVideo, ".bili.json")));
        }
        catch (Exception ex)
        {
            ok = false;
            lines.Add("FAIL  exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "maintenance-check.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, lines);
        }
        catch
        {
            // 写不了日志也不影响退出码
        }

        if (AttachConsole(-1))
        {
            foreach (var line in lines) Console.WriteLine(line);
        }

        Shutdown(ok ? 0 : 1);
    }
}
