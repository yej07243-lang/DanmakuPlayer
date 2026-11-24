using System.IO;
using System.Text;

namespace DanmakuPlayerWin;

/// <summary>
/// 应用级诊断日志，对齐 macOS 版的 Diagnostics：写 %LOCALAPPDATA%\DanmakuPlayer\diagnostics.log，
/// 超过 2 MB 就重建；导出按钮直接把这份文件复制出去。
/// </summary>
public static class Diagnostics
{
    private static readonly object Lock = new();

    public static string LogPath => Path.Combine(Directory, "diagnostics.log");

    public static string Directory => AppPaths.DataDirectory;

    public static void Log(string message)
    {
        try
        {
            var path = LogPath;
            lock (Lock)
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);

                var line = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {message}{Environment.NewLine}";
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写不了不能影响播放
        }
    }
}
