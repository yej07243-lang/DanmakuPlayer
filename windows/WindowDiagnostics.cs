using System.Runtime.InteropServices;
using System.Text;

namespace DanmakuPlayerWin;

internal static class WindowDiagnostics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    public static object DpiInfo(IntPtr topLevel, System.Windows.FrameworkElement element)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
        return new
        {
            WindowDpi = GetDpiForWindow(topLevel),
            VisualDpiScaleX = dpi.DpiScaleX,
            VisualDpiScaleY = dpi.DpiScaleY,
            ElementDips = new { element.ActualWidth, element.ActualHeight }
        };
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "null";
        GetWindowRect(hwnd, out var r);
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        return $"hwnd=0x{hwnd.ToInt64():X} class={cls} rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) size={r.Right - r.Left}x{r.Bottom - r.Top} parent=0x{GetParent(hwnd).ToInt64():X}";
    }

    public static List<string> Snapshot(IntPtr root)
    {
        var lines = new List<string> { "root: " + Describe(root) };
        var children = new List<IntPtr>();
        EnumChildWindows(root, (h, _) =>
        {
            if (children.Count < 8) children.Add(h);
            return true;
        }, IntPtr.Zero);
        for (var i = 0; i < children.Count; i++) lines.Add($"child[{i}]: " + Describe(children[i]));
        return lines;
    }
}
