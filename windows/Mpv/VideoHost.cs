using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DanmakuPlayerWin.Mpv;

/// <summary>
/// 给 libmpv 用的原生子窗口。WPF 不能把 HWND 直接画进视觉树，
/// 所以用 HwndHost 托一个普通子窗口，再把它的句柄交给 mpv 的 wid。
/// 子窗口尺寸用设备像素维护，避免高 DPI 下 mpv 的渲染面比实际可见区域大。
/// </summary>
public sealed class VideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public IntPtr ChildHandle { get; private set; }

    public event EventHandler? HandleReady;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        GetClientRect(hwndParent.Handle, out var rect);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        ChildHandle = CreateWindowEx(
            0, "static", string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0, 0, width, height,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (ChildHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"创建视频宿主窗口失败，Win32 错误 {err}");
        }

        HandleReady?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, ChildHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        ChildHandle = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResizeChildToDevicePixels();
    }

    private void ResizeChildToDevicePixels()
    {
        if (ChildHandle == IntPtr.Zero) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        SetWindowPos(ChildHandle, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate);
    }
}
