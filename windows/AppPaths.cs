using System.IO;

namespace DanmakuPlayerWin;

/// <summary>
/// 数据目录解析：默认 <c>%LOCALAPPDATA%\DanmakuPlayer</c>，可用环境变量 <c>DANMAKU_DATA_DIR</c> 覆盖
/// （对齐 macOS 版的同名变量，方便测试和迁移时把数据放到独立目录）。
/// </summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DANMAKU_DATA_DIR");
            var directory = string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanmakuPlayer")
                : custom;
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string CacheDirectory => Path.Combine(DataDirectory, "danmaku-cache");

    public static string ThumbnailDirectory => Path.Combine(DataDirectory, "thumbnails");

    public static string LibraryDatabase => Path.Combine(DataDirectory, "library.sqlite");
}
