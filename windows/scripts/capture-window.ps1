param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitMilliseconds = 2500
)

Add-Type -AssemblyName System.Drawing

$sig = @'
using System;
using System.Runtime.InteropServices;
public static class WinCap2
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll")] public static extern bool SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);
}
'@

if (-not ('WinCap2' -as [type])) { Add-Type -TypeDefinition $sig }

# Make this process per-monitor DPI aware so GetWindowRect and BitBlt speak physical pixels.
[void][WinCap2]::SetProcessDpiAwarenessContext([IntPtr](-4))

$proc = Get-Process -Id $ProcessId -ErrorAction Stop
for ($i = 0; $i -lt 40 -and $proc.MainWindowHandle -eq 0; $i++) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
}
if ($proc.MainWindowHandle -eq 0) { throw "process $ProcessId has no main window" }

$hwnd = $proc.MainWindowHandle
[void][WinCap2]::ShowWindow($hwnd, 9)
# HWND_TOPMOST = -1, SWP_NOMOVE|SWP_NOSIZE = 2|1
[void][WinCap2]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0002 -bor 0x0001 -bor 0x0040)
[void][WinCap2]::SetForegroundWindow($hwnd)

# 前台必须是目标窗口，否则抓到的会是别的窗口（曾抓到 Chrome 画面）
$foreground = $false
for ($attempt = 0; $attempt -lt 3 -and -not $foreground; $attempt++) {
    Start-Sleep -Milliseconds 600
    $foreground = ([WinCap2]::GetForegroundWindow() -eq $hwnd)
    if ($foreground) { break }

    # 1) 把自己挪到 (0,0) 再置顶
    [void][WinCap2]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0002 -bor 0x0001 -bor 0x0040)

    # 2) 前台锁：临时把输入队列附到当前前台窗口的线程上，再抢前台
    $fg = [WinCap2]::GetForegroundWindow()
    if ($fg -ne [IntPtr]::Zero) {
        $fgProcessId = [uint32]0
        $fgThread = [WinCap2]::GetWindowThreadProcessId($fg, [ref]$fgProcessId)
        $ourThread = [WinCap2]::GetCurrentThreadId()
        [void][WinCap2]::AttachThreadInput($ourThread, $fgThread, $true)
        [void][WinCap2]::SetForegroundWindow($hwnd)
        [void][WinCap2]::AttachThreadInput($ourThread, $fgThread, $false)
    }

    # 3) 最后的手段：SwitchToThisWindow（不要求调用方已在前台）
    [void][WinCap2]::SwitchToThisWindow($hwnd, $false)
    Start-Sleep -Milliseconds 400
    $foreground = ([WinCap2]::GetForegroundWindow() -eq $hwnd)
}
if (-not $foreground) {
    Write-Warning "target window is not foreground; falling back to PrintWindow (image may not match what is on screen)"
}
Start-Sleep -Milliseconds $WaitMilliseconds

$rect = New-Object WinCap2+RECT
if (-not [WinCap2]::GetWindowRect($hwnd, [ref]$rect)) { throw "GetWindowRect failed" }
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "invalid window size ${w}x${h}" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Black)
$method = 'BitBlt(screen, foreground verified)'
$ok = $false

if ($foreground) {
    $screenDc = [WinCap2]::GetDC([IntPtr]::Zero)
    $hdcBmp = $g.GetHdc()
    $ok = [WinCap2]::BitBlt($hdcBmp, 0, 0, $w, $h, $screenDc, $rect.Left, $rect.Top, 0x00CC0020)
    $g.ReleaseHdc($hdcBmp)
    [void][WinCap2]::ReleaseDC([IntPtr]::Zero, $screenDc)
}
else {
    # 前台被别的窗口占着时退化成 PrintWindow：只保证拿到本窗口自己的内容，不代表屏幕当前画面
    $hdcBmp = $g.GetHdc()
    $ok = [WinCap2]::PrintWindow($hwnd, $hdcBmp, 2)
    $g.ReleaseHdc($hdcBmp)
    $method = 'PrintWindow(fallback, foreground NOT verified)'
}
$g.Dispose()

# restore normal z-order
[void][WinCap2]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0002 -bor 0x0001 -bor 0x0040)

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

$samples = 0
$darkSamples = 0
for ($y = 4; $y -lt $h; $y += [Math]::Max(1, [int]($h / 16))) {
    for ($x = 4; $x -lt $w; $x += [Math]::Max(1, [int]($w / 16))) {
        $c = $bmp.GetPixel($x, $y)
        $samples++
        if ($c.R -lt 8 -and $c.G -lt 8 -and $c.B -lt 8) { $darkSamples++ }
    }
}
$bmp.Dispose()

[pscustomobject]@{
    ProcessId   = $ProcessId
    WindowTitle = $proc.MainWindowTitle
    Width       = $w
    Height      = $h
    Method      = $method
    CaptureOk   = $ok
    AllDark     = ($samples -gt 0 -and $darkSamples -eq $samples)
    SavedTo     = $Out
    Bytes       = (Get-Item $Out).Length
} | Format-List
