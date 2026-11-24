param(
    [Parameter(Mandatory = $true)][int]$ProcessId
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$winEnum = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class ProbeWinEnum
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

    public static IntPtr FindVisible(uint processId, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != processId || !IsWindowVisible(hWnd)) return true;
            var text = new StringBuilder(256);
            GetWindowText(hWnd, text, 256);
            if (text.ToString() == title) { found = hWnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
if (-not ('ProbeWinEnum' -as [type])) { Add-Type -TypeDefinition $winEnum }

function Find-ByName {
    param($Root, [string]$Name)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element {
    param($Element)
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-SettingsWindow {
    param([int]$TargetProcessId)
    for ($i = 0; $i -lt 40; $i++) {
        $hwnd = [ProbeWinEnum]::FindVisible([uint32]$TargetProcessId, "弹幕设置")
        if ($hwnd -ne [IntPtr]::Zero) {
            return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

$process = Get-Process -Id $ProcessId
$settings = Get-SettingsWindow -TargetProcessId $ProcessId
if ($null -eq $settings) {
    $player = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $gear = Find-ByName $player "弹幕设置"
    if ($null -eq $gear) { throw "找不到弹幕设置按钮" }
    Invoke-Element $gear
    $settings = Get-SettingsWindow -TargetProcessId $ProcessId
}
if ($null -eq $settings) { throw "弹幕设置窗口没打开" }

$tab = Find-ByName $settings "时间校准"
$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 500

$biliBox = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "BiliTimeBox")))
$biliBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("16.5")
Invoke-Element (Find-ByName $settings "用当前播放位置加锚点")
Start-Sleep -Milliseconds 800

$offsetBox = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "OffsetBox")))
$offsetBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("2.5")
Invoke-Element (Find-ByName $settings "应用")
Start-Sleep -Milliseconds 500

$anchorList = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "AnchorList")))
$items = $anchorList.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)
$anchorTexts = @()
foreach ($item in $items) { $anchorTexts += $item.Current.Name }

$status = $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "CalibrationStatus")))

@{
    Window = $settings.Current.Name
    Status = $status.Current.Name
    Anchors = $anchorTexts
    Offset = $offsetBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
} | ConvertTo-Json -Depth 4
