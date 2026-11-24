using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DanmakuPlayerWin.Playback;
using DanmakuPlayerWin.Theme;
using Microsoft.Win32;

namespace DanmakuPlayerWin;

/// <summary>
/// 应用级设置：外观（主题跟随系统/浅色/深色）、弹幕总开关与字号、数据目录与日志。
/// 改动立即写入设置文件，并通过回调同步给已经打开的播放窗口。
/// </summary>
public partial class AppSettingsWindow : Window
{
    private readonly Action<DanmakuSettings> _onChanged;
    private readonly DanmakuSettings _settings;
    private bool _loading = true;

    public AppSettingsWindow(Action<DanmakuSettings> onChanged)
    {
        InitializeComponent();
        _onChanged = onChanged;
        _settings = DanmakuSettingsStore.Load();

        _loading = true;
        EnabledCheck.IsChecked = _settings.Enabled;
        FontSlider.Value = _settings.FontSize;

        ThemeBox.Items.Add(new ComboBoxItem { Content = "跟随系统", Tag = ThemeMode.System });
        ThemeBox.Items.Add(new ComboBoxItem { Content = "浅色", Tag = ThemeMode.Light });
        ThemeBox.Items.Add(new ComboBoxItem { Content = "深色", Tag = ThemeMode.Dark });
        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().First(i => (ThemeMode)i.Tag == ThemeManager.Mode);
        _loading = false;

        UpdateFontLabel();
        VersionText.Text = $"版本 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";
        DataDirText.Text = AppPaths.DataDirectory;
    }

    private void UpdateFontLabel() => FontValue.Text = $"{_settings.FontSize:0}";

    private void Apply()
    {
        DanmakuSettingsStore.Save(_settings);
        _onChanged(_settings.Clone());
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: ThemeMode mode }) ThemeManager.SetMode(mode);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow { Owner = this };
        window.ShowDialog();
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        _settings.Enabled = EnabledCheck.IsChecked == true;
        Apply();
    }

    private void OnFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsInitialized) return;
        _settings.FontSize = Math.Round(FontSlider.Value);
        UpdateFontLabel();
        Apply();
    }

    private void OnOpenDataClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开数据目录失败：{ex.Message}", "DanmakuPlayer");
        }
    }

    private void OnExportLogsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断日志",
            FileName = "DanmakuPlayer-diagnostics.log",
            Filter = "日志文件|*.log|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (File.Exists(Diagnostics.LogPath)) File.Copy(Diagnostics.LogPath, dialog.FileName, overwrite: true);
            else File.WriteAllText(dialog.FileName, "暂无日志" + Environment.NewLine);
            MessageBox.Show(this, "日志已导出。", "DanmakuPlayer");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "DanmakuPlayer");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
