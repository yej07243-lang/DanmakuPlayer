using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DanmakuPlayerWin.Playback;
using Microsoft.Win32;

namespace DanmakuPlayerWin;

/// <summary>
/// 播放器的「弹幕设置」，按 mac 版分成「显示 / 时间校准 / 来源与缓存」三页：
/// 显示页跨影片生效并立即写入设置文件；校准按当前影片保存；来源页复用播放器的载入/刷新/清除逻辑。
/// </summary>
public partial class DanmakuSettingsWindow : Window
{
    /// <summary>B 站播放器弹幕字体列表里能在 Windows 上落地的几项。</summary>
    private static readonly (string Label, string Family)[] FontChoices =
    {
        ("微软雅黑", "Microsoft YaHei"),
        ("黑体", "SimHei"),
        ("宋体", "SimSun"),
        ("仿宋", "FangSong"),
        ("等线", "DengXian"),
        ("楷体", "KaiTi")
    };

    private readonly MainWindow _owner;
    private DanmakuSettings _settings;
    private bool _updating;

    public DanmakuSettingsWindow(MainWindow owner, int initialTab = 0)
    {
        InitializeComponent();
        _owner = owner;
        _settings = owner.Settings.Clone();

        foreach (var (label, family) in FontChoices)
            FontBox.Items.Add(new ComboBoxItem { Content = label, Tag = family });

        LoadIntoControls();
        RefreshCalibration();
        RefreshSource();

        if (initialTab > 0 && initialTab < Tabs.Items.Count) Tabs.SelectedIndex = initialTab;
    }

    private void LoadIntoControls()
    {
        _updating = true;
        FontSlider.Value = _settings.FontSize;
        OpacitySlider.Value = _settings.Opacity;
        AreaSlider.Value = _settings.Area;
        SpeedSlider.Value = _settings.ScrollSpeed;
        MaxVisibleSlider.Value = _settings.MaxVisible;
        ScrollingCheck.IsChecked = _settings.Scrolling;
        TopCheck.IsChecked = _settings.Top;
        BottomCheck.IsChecked = _settings.Bottom;
        BoldCheck.IsChecked = _settings.Bold;
        BlockedBox.Text = _settings.BlockedWords;

        var match = FontBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, _settings.FontName, StringComparison.OrdinalIgnoreCase));
        FontBox.SelectedItem = match ?? FontBox.Items[0];
        _updating = false;

        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        FontValue.Text = $"{_settings.FontSize:0}";
        OpacityValue.Text = $"{_settings.Opacity:0.00}";
        AreaValue.Text = $"{_settings.Area:0.00}";
        SpeedValue.Text = $"{_settings.ScrollSpeed:0.00}";
        MaxVisibleValue.Text = $"{_settings.MaxVisible}";
    }

    private void RefreshAll()
    {
        RefreshCalibration();
        RefreshSource();
    }

    private void RefreshCalibration()
    {
        var offset = _owner.OffsetSeconds;
        OffsetBox.Text = offset.ToString("0.###", CultureInfo.InvariantCulture);

        AnchorList.Items.Clear();
        var anchors = _owner.Anchors;
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            AnchorList.Items.Add($"{i + 1}.  本地 {CalibrationStore.FormatTime(anchor.Local)}  →  B 站 {CalibrationStore.FormatTime(anchor.Bili)}");
        }

        var sign = offset > 0 ? "+" : string.Empty;
        CalibrationStatus.Text =
            $"整体延后 {sign}{offset:0.###} 秒 · {_owner.CalibrationSummary}\n" +
            "正数让弹幕更晚出现；分段锚点按「本地画面时刻 ↔ B 站秒数」对齐。";
    }

    private void RefreshSource()
    {
        var file = _owner.CurrentFile;
        SourceStatus.Text =
            $"当前影片：{(string.IsNullOrEmpty(file) ? "未打开" : Path.GetFileName(file))}\n" +
            $"弹幕：{_owner.DanmakuSourceSummary}";
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        if (FontBox.SelectedItem is ComboBoxItem { Tag: string family })
        {
            _settings.FontName = family;
            _owner.ApplySettings(_settings.Clone());
        }
    }

    private void OnBoldChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        _settings.Bold = BoldCheck.IsChecked == true;
        _owner.ApplySettings(_settings.Clone());
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载期间 Slider 初始化也会触发 ValueChanged，此时后面的控件还没创建
        if (_updating || !IsInitialized) return;

        _settings.FontSize = Math.Round(FontSlider.Value);
        _settings.Opacity = Math.Round(OpacitySlider.Value, 2);
        _settings.Area = Math.Round(AreaSlider.Value, 2);
        _settings.ScrollSpeed = Math.Round(SpeedSlider.Value, 2);
        _settings.MaxVisible = (int)Math.Round(MaxVisibleSlider.Value);

        UpdateValueLabels();
        _owner.ApplySettings(_settings.Clone());
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        _settings.Scrolling = ScrollingCheck.IsChecked == true;
        _settings.Top = TopCheck.IsChecked == true;
        _settings.Bottom = BottomCheck.IsChecked == true;
        _owner.ApplySettings(_settings.Clone());
    }

    private void OnBlockedChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || !IsInitialized) return;
        _settings.BlockedWords = BlockedBox.Text;
        _owner.ApplySettings(_settings.Clone());
    }

    private void OnOffsetEarlierClick(object sender, RoutedEventArgs e)
    {
        _owner.NudgeOffset(-0.5);
        RefreshCalibration();
    }

    private void OnOffsetLaterClick(object sender, RoutedEventArgs e)
    {
        _owner.NudgeOffset(0.5);
        RefreshCalibration();
    }

    private void OnApplyOffsetClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(OffsetBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            StanceText.Text = "整体延后要填数字，例如 12.5 或 -3";
            return;
        }

        _owner.SetOffset(value);
        StanceText.Text = $"整体延后已设为 {value:0.###} 秒";
        RefreshCalibration();
    }

    private void OnAddAnchorClick(object sender, RoutedEventArgs e)
    {
        if (!CalibrationStore.TryParseTime(BiliTimeBox.Text, out var biliSeconds))
        {
            StanceText.Text = "B 站时间填 12:34.5、1:02:03 或纯秒数 754.5";
            return;
        }

        if (!_owner.AddAnchorHere(biliSeconds))
        {
            StanceText.Text = "当前拿不到播放位置，先播放到要打点的画面再试";
            return;
        }

        BiliTimeBox.Text = string.Empty;
        StanceText.Text = "已按当前播放位置添加锚点（相距 0.1 秒内的旧点会被替换）";
        RefreshCalibration();
    }

    private void OnRemoveAnchorClick(object sender, RoutedEventArgs e)
    {
        var index = AnchorList.SelectedIndex;
        if (index < 0)
        {
            StanceText.Text = "先在列表里选中一个锚点";
            return;
        }

        _owner.RemoveAnchor(index);
        StanceText.Text = "已删除选中锚点";
        RefreshCalibration();
    }

    private void OnClearAnchorsClick(object sender, RoutedEventArgs e)
    {
        if (_owner.Anchors.Count == 0)
        {
            StanceText.Text = "当前没有锚点";
            return;
        }

        var answer = MessageBox.Show(this, "清空当前影片的所有校准锚点？整体延后会保留。",
            "清空锚点", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        _owner.ClearAnchors();
        StanceText.Text = "已清空锚点";
        RefreshCalibration();
    }

    private void OnLoadXmlClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择弹幕 XML",
            Filter = "弹幕 XML|*.xml|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        _owner.ImportLocalDanmaku(dialog.FileName);
        RefreshAll();
    }

    private void OnBiliClick(object sender, RoutedEventArgs e)
    {
        var window = new BiliWindow(_owner) { Owner = this };
        window.ShowDialog();
        RefreshAll();
    }

    private void OnRefreshBiliClick(object sender, RoutedEventArgs e)
    {
        _owner.RefreshBiliDanmakuPublic();
        RefreshAll();
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "清除当前来源的缓存和画面弹幕？B 站关联仍保留，可刷新重新下载；本地 XML 会删除应用内副本。",
            "清除弹幕缓存", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _owner.ClearDanmakuCachePublic();
        RefreshAll();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _settings = new DanmakuSettings();
        LoadIntoControls();
        _owner.ApplySettings(_settings.Clone());
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
