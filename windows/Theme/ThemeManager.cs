using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DanmakuPlayerWin.Theme;

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// 主题：默认跟随 Windows 的「应用模式」注册表项，也可以在设置里锁定浅色/深色。
/// 两套配色是同名的画刷资源，切换时整体替换合并字典，所有 DynamicResource 立即生效。
/// </summary>
public static class ThemeManager
{
    private sealed record Saved(ThemeMode Mode);

    private static ResourceDictionary? _current;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static bool IsDark { get; private set; } = true;

    public static string SettingsPath => Path.Combine(AppPaths.DataDirectory, "appearance.json");

    public static event EventHandler? ThemeChanged;

    public static void Initialize()
    {
        Mode = LoadMode();
        ApplyDictionary(ResolveDark(Mode));
        ApplyUiFont();
    }

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        SaveMode(mode);
        ApplyDictionary(ResolveDark(mode));
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>系统主题切换后调用：只在「跟随系统」模式下真的换色。</summary>
    public static void RefreshSystemTheme()
    {
        if (Mode != ThemeMode.System) return;

        var wantDark = SystemUsesLightTheme() == false;
        if (wantDark == IsDark) return;

        ApplyDictionary(wantDark);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string ModeLabel => Mode switch
    {
        ThemeMode.Light => "浅色",
        ThemeMode.Dark => "深色",
        _ => "跟随系统"
    };

    private static void ApplyDictionary(bool dark)
    {
        var app = Application.Current;
        if (app == null) return;

        IsDark = dark;
        var next = new ResourceDictionary
        {
            Source = new Uri(dark ? "/Theme/ThemeDark.xaml" : "/Theme/ThemeLight.xaml", UriKind.Relative)
        };

        if (_current != null) app.Resources.MergedDictionaries.Remove(_current);
        // 后加入的字典优先级更高，必须追加而不是插到最前面
        app.Resources.MergedDictionaries.Add(next);
        _current = next;
    }

    private static void ApplyUiFont()
    {
        var app = Application.Current;
        if (app == null) return;
        app.Resources["UiFont"] = new FontFamily("Microsoft YaHei UI");
    }

    private static bool ResolveDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => SystemUsesLightTheme() == false
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private static ThemeMode LoadMode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ThemeMode.System;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SettingsPath));
            return saved?.Mode is ThemeMode.Light or ThemeMode.Dark ? saved.Mode : ThemeMode.System;
        }
        catch
        {
            return ThemeMode.System;
        }
    }

    private static void SaveMode(ThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Saved(mode), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 存不下主题偏好不影响使用
        }
    }
}
