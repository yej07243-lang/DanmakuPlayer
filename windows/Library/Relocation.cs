using System.IO;

namespace DanmakuPlayerWin.Library;

/// <summary>
/// 文件重新定位时的附属数据迁移：弹幕边车和 B 站来源记录跟着视频路径走，
/// 观看进度和校准由 <see cref="Playback.ResumeStore"/> / <see cref="Playback.CalibrationStore"/> 的 Move 处理。
/// </summary>
public static class Relocation
{
    /// <summary>把旧路径旁的 <c>.xml</c> 弹幕和 <c>.bili.json</c> 来源记录复制到新路径，已有文件不覆盖。</summary>
    public static void MigrateSidecars(string oldMediaPath, string newMediaPath)
    {
        foreach (var extension in new[] { ".xml", ".bili.json" })
        {
            try
            {
                var oldSidecar = Path.ChangeExtension(oldMediaPath, extension);
                var newSidecar = Path.ChangeExtension(newMediaPath, extension);
                if (File.Exists(oldSidecar) && !File.Exists(newSidecar))
                    File.Copy(oldSidecar, newSidecar);
            }
            catch
            {
                // 边车不是定位的必要条件，复制失败不影响媒体库记录
            }
        }
    }
}
