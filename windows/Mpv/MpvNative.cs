using System.Runtime.InteropServices;

namespace DanmakuPlayerWin.Mpv;

/// <summary>libmpv C API 鐨勬渶灏忕粦瀹氾紝鍙０鏄庡綋鍓嶇敤鍒扮殑閭ｉ儴鍒嗐€?/summary>
internal static class MpvNative
{
    private const string Dll = "libmpv-2.dll";

    // mpv_format
    internal const int FormatNone = 0;
    internal const int FormatString = 1;
    internal const int FormatFlag = 3;
    internal const int FormatInt64 = 4;
    internal const int FormatDouble = 5;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr ctx, IntPtr args);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_error_string(int error);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong mpv_client_api_version();

    internal static string ErrorText(int code)
    {
        var p = mpv_error_string(code);
        return p == IntPtr.Zero ? $"error {code}" : Marshal.PtrToStringUTF8(p) ?? $"error {code}";
    }
}
