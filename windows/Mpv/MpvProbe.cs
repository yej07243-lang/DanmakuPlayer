using System.Runtime.InteropServices;

namespace DanmakuPlayerWin.Mpv;

/// <summary>
/// 无窗口的 libmpv 探测：只读片长和内置标题，用作 ffprobe 缺失时的兜底。
/// 发布包只带 libmpv，干净机器上没有 ffprobe 时媒体库仍能拿到片长。
/// </summary>
public static class MpvProbe
{
    public static (double Duration, string? Title) Probe(string path, int timeoutMilliseconds = 8000)
    {
        var context = IntPtr.Zero;
        try
        {
            context = MpvNative.mpv_create();
            if (context == IntPtr.Zero) return (0, null);

            MpvNative.mpv_set_option_string(context, "config", "no");
            MpvNative.mpv_set_option_string(context, "terminal", "no");
            MpvNative.mpv_set_option_string(context, "vo", "null");
            MpvNative.mpv_set_option_string(context, "ao", "null");
            MpvNative.mpv_set_option_string(context, "idle", "yes");
            MpvNative.mpv_set_option_string(context, "pause", "yes");
            MpvNative.mpv_set_option_string(context, "input-default-bindings", "no");
            MpvNative.mpv_set_option_string(context, "input-vo-keyboard", "no");

            if (MpvNative.mpv_initialize(context) < 0) return (0, null);
            if (Command(context, "loadfile", path) < 0) return (0, null);

            var deadline = Environment.TickCount64 + timeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                var duration = ReadDouble(context, "duration");
                if (duration > 0) return (duration, ReadString(context, "media-title"));

                // 文件打不开时 mpv 回到 idle，不必等满超时
                if (ReadFlag(context, "idle-active")) break;
                Thread.Sleep(40);
            }

            var fallback = ReadDouble(context, "duration");
            return (fallback > 0 ? fallback : 0, ReadString(context, "media-title"));
        }
        catch
        {
            return (0, null);
        }
        finally
        {
            if (context != IntPtr.Zero) MpvNative.mpv_terminate_destroy(context);
        }
    }

    private static int Command(IntPtr context, params string[] arguments)
    {
        var pointers = new IntPtr[arguments.Length + 1];
        var array = IntPtr.Zero;
        try
        {
            for (var i = 0; i < arguments.Length; i++)
                pointers[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            array = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
            Marshal.Copy(pointers, 0, array, pointers.Length);
            return MpvNative.mpv_command(context, array);
        }
        finally
        {
            foreach (var pointer in pointers)
                if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            if (array != IntPtr.Zero) Marshal.FreeHGlobal(array);
        }
    }

    private static double ReadDouble(IntPtr context, string name) =>
        MpvNative.mpv_get_property(context, name, MpvNative.FormatDouble, out double value) < 0 ? double.NaN : value;

    private static string? ReadString(IntPtr context, string name)
    {
        var pointer = MpvNative.mpv_get_property_string(context, name);
        if (pointer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(pointer); }
        finally { MpvNative.mpv_free(pointer); }
    }

    private static bool ReadFlag(IntPtr context, string name) =>
        MpvNative.mpv_get_property(context, name, MpvNative.FormatFlag, out int value) >= 0 && value != 0;
}
