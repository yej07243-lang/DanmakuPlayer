using System.IO;
using System.Runtime.InteropServices;

namespace DanmakuPlayerWin.Mpv;

/// <summary>一个 libmpv 实例：创建、加载、查询属性。视频由 mpv 自己渲染到传入的窗口句柄。</summary>
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private bool _disposed;

    public IntPtr Handle => _ctx;
    public string? LogPath { get; }
    public string? CurrentFile { get; private set; }

    public MpvPlayer(IntPtr windowHandle, string? logPath = null)
    {
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("需要一个有效的窗口句柄", nameof(windowHandle));

        _ctx = MpvNative.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() 失败：libmpv-2.dll 未加载?");

        LogPath = logPath;

        // 渲染目标必须先于 initialize 设定，mpv 会在该窗口里建自己的子窗口。
        SetOption("wid", windowHandle.ToInt64().ToString());

        SetOption("config", "no");
        SetOption("terminal", "no");
        SetOption("msg-level", "all=v");
        if (!string.IsNullOrEmpty(logPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            SetOption("log-file", logPath);
        }

        SetOption("vo", "gpu");
        SetOption("hwdec", "auto-safe");
        SetOption("hidpi-window-scale", "no");
        SetOption("idle", "yes");
        SetOption("keep-open", "yes");
        SetOption("osc", "no");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("sub-auto", "fuzzy");
        SetOption("audio-file-auto", "fuzzy");
        SetOption("volume", "80");
        SetOption("user-agent", "DanmakuPlayer-win/0.1");

        var rc = MpvNative.mpv_initialize(_ctx);
        if (rc < 0) throw new InvalidOperationException($"mpv_initialize 失败：{MpvNative.ErrorText(rc)}");
    }

    public void SetOption(string name, string value)
    {
        ThrowIfDisposed();
        var rc = MpvNative.mpv_set_option_string(_ctx, name, value);
        if (rc < 0) throw new InvalidOperationException($"设置选项 {name}={value} 失败：{MpvNative.ErrorText(rc)}");
    }

    public void SetProperty(string name, string value)
    {
        ThrowIfDisposed();
        var rc = MpvNative.mpv_set_property_string(_ctx, name, value);
        if (rc < 0) throw new InvalidOperationException($"设置属性 {name}={value} 失败：{MpvNative.ErrorText(rc)}");
    }

    public int Command(params string[] args)
    {
        ThrowIfDisposed();
        var ptrs = new IntPtr[args.Length + 1];
        var array = IntPtr.Zero;
        try
        {
            // libmpv 全程用 UTF-8，中文路径必须走 UTF-8 而不是系统 ANSI 代码页
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            array = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            Marshal.Copy(ptrs, 0, array, ptrs.Length);
            return MpvNative.mpv_command(_ctx, array);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            if (array != IntPtr.Zero) Marshal.FreeHGlobal(array);
        }
    }

    public void LoadFile(string path)
    {
        CurrentFile = Path.GetFullPath(path);
        var rc = Command("loadfile", CurrentFile, "replace");
        if (rc < 0) throw new InvalidOperationException($"加载 {CurrentFile} 失败：{MpvNative.ErrorText(rc)}");
    }

    public void Play() => Command("set", "pause", "no");
    public void Pause() => Command("set", "pause", "yes");
    public void TogglePause() => Command("cycle", "pause");
    public void SeekRelative(double seconds) => Command("seek", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "relative");
    public void SeekAbsolute(double seconds) => Command("seek", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute");

    public string? GetString(string name)
    {
        ThrowIfDisposed();
        var p = MpvNative.mpv_get_property_string(_ctx, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public double GetDouble(string name)
    {
        ThrowIfDisposed();
        return MpvNative.mpv_get_property(_ctx, name, MpvNative.FormatDouble, out double v) < 0 ? double.NaN : v;
    }

    public long GetInt(string name)
    {
        ThrowIfDisposed();
        return MpvNative.mpv_get_property(_ctx, name, MpvNative.FormatInt64, out long v) < 0 ? -1 : v;
    }

    public bool GetFlag(string name)
    {
        ThrowIfDisposed();
        return MpvNative.mpv_get_property(_ctx, name, MpvNative.FormatFlag, out int v) >= 0 && v != 0;
    }

    public string Screenshot(string path, bool withSubtitles = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rc = Command("screenshot-to-file", path, withSubtitles ? "subtitles" : "video");
        return rc < 0 ? $"失败：{MpvNative.ErrorText(rc)}" : path;
    }

    /// <summary>重新从磁盘读当前字幕文件。校准改动后用它热更新弹幕，不动播放状态。</summary>
    public bool SubReload() => Command("sub-reload") >= 0;

    /// <summary>当前时间点正在显示的字幕文本，自检用它断言校准是否正确。</summary>
    public string? GetSubText() => GetString("sub-text");

    public void SetVolume(double volume) =>
        SetProperty("volume", volume.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));

    public double GetVolume() => GetDouble("volume");

    public void SetSpeed(double speed) =>
        SetProperty("speed", speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

    public double GetSpeed() => GetDouble("speed");

    public void SetAudioTrack(int id) => SetProperty("aid", id.ToString());

    /// <summary>弹幕占着主字幕轨，所以用户字幕走 secondary-sid，两者可以同时显示。</summary>
    public void SetSubtitleTrack(int? id, bool secondary) =>
        SetProperty(secondary ? "secondary-sid" : "sid", id?.ToString() ?? "no");

    public double GetSecondarySubtitleDelay()
    {
        var value = GetDouble("secondary-sub-delay");
        return double.IsNaN(value) ? 0 : value;
    }

    /// <summary>次字幕轨（用户外挂字幕）当前显示的文本。</summary>
    public string? GetSecondarySubtitleText() => GetString("secondary-sub-text");

    /// <summary>外挂字幕的延迟；正数让字幕更晚出现。</summary>
    public void NudgeSecondarySubtitleDelay(double delta) =>
        SetProperty("secondary-sub-delay",
            (GetSecondarySubtitleDelay() + delta).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>加载外挂字幕并返回新轨道 id。故意不带 select：主字幕轨要留给弹幕。</summary>
    public int? AddSubtitle(string file)
    {
        var before = GetTracks().Where(t => t.Type == "sub").Select(t => t.Id).DefaultIfEmpty(0).Max();
        if (Command("sub-add", file) < 0) return null;
        return GetTracks().Where(t => t.Type == "sub" && t.Id > before).Select(t => (int?)t.Id).FirstOrDefault();
    }

    /// <summary>删掉所有字幕轨（含我们自己的弹幕轨），用来保证弹幕只挂一条。</summary>
    public void RemoveAllSubtitles()
    {
        Command("sub-remove");
        SetProperty("secondary-sid", "no");
    }

    public sealed record MpvTrack(int Id, string Type, string Label, bool Selected, bool External = false, string? ExternalFile = null);

    public IReadOnlyList<MpvTrack> GetTracks()
    {
        var json = GetString("track-list");
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<MpvTrack>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var list = new List<MpvTrack>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var idElement)) continue;
                if (!row.TryGetProperty("type", out var typeElement)) continue;

                var parts = new[] { "title", "lang", "codec" }
                    .Select(key => row.TryGetProperty(key, out var value) ? value.GetString() : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text));
                var label = string.Join(" · ", parts);
                if (label.Length == 0) label = $"轨道 {idElement.GetInt32()}";

                list.Add(new MpvTrack(
                    idElement.GetInt32(),
                    typeElement.GetString() ?? string.Empty,
                    label,
                    row.TryGetProperty("selected", out var selected) && selected.ValueKind == System.Text.Json.JsonValueKind.True,
                    row.TryGetProperty("external", out var external) && external.ValueKind == System.Text.Json.JsonValueKind.True,
                    row.TryGetProperty("external-filename", out var externalFile) ? externalFile.GetString() : null));
            }
            return list;
        }
        catch
        {
            return Array.Empty<MpvTrack>();
        }
    }

    public static string ClientApiVersion()
    {
        var v = MpvNative.mpv_client_api_version();
        return $"{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MpvPlayer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ctx != IntPtr.Zero)
        {
            MpvNative.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
