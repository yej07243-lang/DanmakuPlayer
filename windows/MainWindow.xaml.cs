using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DanmakuPlayerWin.Bili;
using DanmakuPlayerWin.Danmaku;
using DanmakuPlayerWin.Library;
using DanmakuPlayerWin.Mpv;
using DanmakuPlayerWin.Playback;
using Microsoft.Win32;

namespace DanmakuPlayerWin;

public partial class MainWindow : Window
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectStruct Monitor;
        public RectStruct Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ResumeStore _resume = new();
    private DanmakuSettings _settings = DanmakuSettingsStore.Load();
    private readonly double[] _speeds = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
    private MpvPlayer? _player;
    private DanmakuTrack? _track;
    private Calibration? _calibration;
    private string? _danmakuFile;
    private string? _assPath;
    private string? _danmakuSubId;
    private bool _subAdded;
    private int _droppedDanmaku;
    private int _filteredDanmaku;
    private string? _pendingFile;
    private string? _preferredTitle;
    private string? _currentFile;
    private double? _pendingSeek;
    private double? _resumeTarget;
    private double _offsetSeconds;
    private bool _suppressResume;
    private bool _seeking;
    private bool _updatingUi;
    private double _volumeBeforeMute = 80;
    private string _trackSignature = string.Empty;
    private bool _keepAwake;
    private int _idleTicks;
    private bool _isFullscreen;
    private WindowStyle _savedStyle;
    private WindowState _savedState;
    private ResizeMode _savedResize;
    private Rect _savedBounds;
    private int _handleWaitTicks;
    private int _tickCount;
    private DateTime _lastResumeSave = DateTime.MinValue;
    private bool _autoSubtitleApplied;
    private bool _watchedThisSession;
    private bool _durationReported;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        Loaded += OnLoaded;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        foreach (var speed in _speeds)
            SpeedBox.Items.Add(new ComboBoxItem { Content = $"{speed:0.##}×", Tag = speed });
        SpeedBox.SelectedIndex = 2;
        _watchedThisSession = false;
    }

    public double CurrentLocalTime => _player?.GetDouble("time-pos") ?? double.NaN;

    public double MediaDuration => _player?.GetDouble("duration") ?? double.NaN;

    public Calibration? Calibration => _calibration;

    public string? CurrentFile => _currentFile;

    public DanmakuSettings Settings => _settings;

    public double OffsetSeconds => _offsetSeconds;

    public void NudgeOffset(double delta) => SetOffset(_offsetSeconds + delta);

    /// <summary>设置整体延后（秒），正数让弹幕更晚出现；按影片保存。</summary>
    public void SetOffset(double value)
    {
        _offsetSeconds = Math.Round(value, 3);
        if (_currentFile != null)
        {
            var calibration = _calibration ?? new Calibration(_track?.Source);
            calibration.OffsetSeconds = _offsetSeconds;
            _calibration = calibration;
            CalibrationStore.Save(_currentFile, calibration);
        }
        ApplyOffset();
    }

    /// <summary>媒体库监听：清除缓存后把条目里的 B 站来源/本地关联一并清掉。</summary>
    public Action<string>? DanmakuCleared { get; set; }

    /// <summary>首次拿到片长时通知媒体库，补上扫描时没读到的时长。</summary>
    public Action<string, double>? DurationReported { get; set; }

    public IReadOnlyList<CalibrationAnchorInfo> Anchors =>
        _calibration?.Anchors.Select(a => new CalibrationAnchorInfo(a.Local, a.Bili)).ToList()
        ?? (IReadOnlyList<CalibrationAnchorInfo>)Array.Empty<CalibrationAnchorInfo>();

    public string DanmakuSourceSummary => _track == null
        ? "未加载弹幕"
        : $"{_track.Source} · {_track.Items.Count} 条" +
          (_droppedDanmaku > 0 ? $" · 丢弃 {_droppedDanmaku}" : string.Empty) +
          (_filteredDanmaku > 0 ? $" · 筛掉 {_filteredDanmaku}" : string.Empty);

    public string CalibrationSummary => _calibration is { IsEmpty: false }
        ? $"{_calibration.Anchors.Count} 个锚点"
        : "未校准";

    /// <summary>在当前播放位置加一个锚点：这里对应 B 站第 <paramref name="biliSeconds"/> 秒。</summary>
    public bool AddAnchorHere(double biliSeconds)
    {
        var local = CurrentLocalTime;
        if (double.IsNaN(local) || local < 0) return false;
        if (double.IsNaN(biliSeconds) || biliSeconds < 0) return false;

        // 相距不到 0.1 秒视为同一个点，替换旧的而不是硬塞一个冲突锚点
        var anchors = (_calibration?.Anchors ?? Array.Empty<Anchor>())
            .Where(a => Math.Abs(a.Local - local) >= 0.1)
            .ToList();
        var calibration = new Calibration(_calibration?.DanmakuSource ?? _track?.Source, anchors)
        {
            OffsetSeconds = _offsetSeconds
        };
        calibration.Add(new Anchor(Math.Round(local, 2), Math.Round(biliSeconds, 2)));

        if (!calibration.TryValidate(out var error))
        {
            ShowError(error ?? "锚点无效");
            return false;
        }

        ApplyCalibration(calibration);
        return true;
    }

    public void RemoveAnchor(int index)
    {
        if (_calibration == null || index < 0 || index >= _calibration.Anchors.Count) return;
        var calibration = new Calibration(_calibration.DanmakuSource, _calibration.Anchors)
        {
            OffsetSeconds = _offsetSeconds
        };
        calibration.RemoveAt(index);
        ApplyCalibration(calibration);
    }

    public void ClearAnchors() =>
        ApplyCalibration(new Calibration(_track?.Source) { OffsetSeconds = _offsetSeconds });

    public void OpenDanmakuFileDialog()
    {
        var dialog = new OpenFileDialog { Title = "选择弹幕 XML", Filter = "弹幕 XML|*.xml|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) ImportLocalDanmaku(dialog.FileName);
    }

    /// <summary>本地 XML 切换为当前影片的弹幕：复制成同名边车并清掉旧的 B 站来源。</summary>
    public void ImportLocalDanmaku(string xmlPath)
    {
        if (_player == null) return;

        if (_currentFile == null)
        {
            LoadDanmaku(xmlPath);
            return;
        }

        var sidecar = Path.ChangeExtension(_currentFile, ".xml");
        try
        {
            if (!string.Equals(Path.GetFullPath(sidecar), Path.GetFullPath(xmlPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(xmlPath, sidecar, overwrite: true);

            var meta = Path.ChangeExtension(_currentFile, ".bili.json");
            if (File.Exists(meta)) File.Delete(meta);

            LoadDanmaku(sidecar);
            StatusText.Text = $"已切换到本地弹幕：{Path.GetFileName(xmlPath)}（已存同名边车）";
            Diagnostics.Log($"local xml media={_currentFile} source={xmlPath}");
        }
        catch (Exception ex)
        {
            // 视频目录只读时退化为本次会话直接加载，不阻断播放
            LoadDanmaku(xmlPath);
            ShowError($"边车保存失败，本次先直接加载：{ex.Message}");
        }
    }

    public void OpenBiliDialogPublic()
    {
        if (_player == null) return;
        var window = new BiliWindow(this, searchTitle: SearchTitleForBili()) { Owner = this };
        window.ShowDialog();
    }

    public void RefreshBiliDanmakuPublic() => OnRefreshBiliClick(this, new RoutedEventArgs());

    public void ClearDanmakuCachePublic() => OnClearDanmakuCacheClick(this, new RoutedEventArgs());

    /// <summary>媒体库注入的播放列表（标题 + 路径），播放器里菜单直接切集。</summary>
    public Func<IReadOnlyList<MediaItem>>? PlaylistProvider { get; set; }

    /// <summary>用户从播放列表里选了别的条目，交给媒体库开新播放器窗口。</summary>
    public Action<MediaItem>? PlayRequested { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
        EnsurePlayer();
    }

    private void EnsurePlayer()
    {
        if (_player != null) return;

        var hwnd = Video.ChildHandle;
        if (hwnd == IntPtr.Zero)
        {
            if (++_handleWaitTicks > 100)
            {
                StatusText.Text = "视频宿主窗口未创建";
                return;
            }
            Dispatcher.BeginInvoke(new Action(EnsurePlayer), DispatcherPriority.Loaded);
            return;
        }

        var log = Path.Combine(AppContext.BaseDirectory, "logs", $"mpv-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _player = new MpvPlayer(hwnd, log);
        _statusTimer.Start();
        _player.SetVolume(VolumeSlider.Value);
        SyncDanmakuToggle();

        if (_pendingFile is { } file)
        {
            _pendingFile = null;
            Open(file);
        }
    }

    public void RequestOpen(string path) => RequestOpen(path, null);

    public void RequestOpen(string path, string? title)
    {
        _preferredTitle = title;
        if (_player == null && !IsLoaded)
        {
            _pendingFile = path;
            return;
        }
        Open(path);
    }

    public void Open(string path)
    {
        EnsurePlayer();
        if (_player == null)
        {
            _pendingFile = path;
            return;
        }

        try
        {
            _player.LoadFile(path);
            _currentFile = Path.GetFullPath(path);
            Title = $"DanmakuPlayer — {_preferredTitle ?? Path.GetFileName(path)}";
            _preferredTitle = null;
            ClearError();
            StatusText.Text = Path.GetFileName(path);
            Diagnostics.Log($"open media={_currentFile}");

            _track = null;
            _calibration = CalibrationStore.Load(_currentFile);
            _danmakuFile = null;
            _assPath = null;
            _subAdded = false;
            _droppedDanmaku = 0;
            _filteredDanmaku = 0;
            _offsetSeconds = _calibration?.OffsetSeconds ?? 0;
            _trackSignature = string.Empty;
            _idleTicks = 0;
            _lastResumeSave = DateTime.MinValue;
            _autoSubtitleApplied = false;
            _watchedThisSession = false;
            _durationReported = false;
            UpdateOffsetText();
            ApplyOffset();
            UpdateCalibrationText();

            var sidecar = Path.ChangeExtension(_currentFile, ".xml");
            if (File.Exists(sidecar)) LoadDanmaku(sidecar);
            else UpdateDanmakuStatus();

            var watched = _resume.IsWatched(_currentFile);
            var saved = watched ? null : _resume.GetPosition(_currentFile);
            _pendingSeek = !_suppressResume && saved is > 5 ? saved : null;
            _resumeTarget = _pendingSeek;
            if (watched)
            {
                _pendingSeek = null;
                _resumeTarget = null;
                StatusText.Text = "已看完，从头播放";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    public void LoadDanmaku(string path)
    {
        if (_player == null) return;

        try
        {
            _track = DanmakuXmlParser.ParseFile(path);
            _danmakuFile = Path.GetFullPath(path);
            _assPath = AssBuilder.AssPathFor(_danmakuFile);
            _subAdded = false;

            _calibration = _currentFile != null ? CalibrationStore.Load(_currentFile) : null;
            var mismatched = _calibration?.DanmakuSource != null &&
                             !string.Equals(_calibration.DanmakuSource, _track.Source, StringComparison.OrdinalIgnoreCase);

            RegenerateAss();

            // 先清掉所有字幕轨：重复加载 XML 会叠出两条同样的弹幕轨，看起来就是重影
            _player.RemoveAllSubtitles();
            _danmakuSubId = null;
            _autoSubtitleApplied = false;
            _player.Command("sub-add", _assPath, "select");
            _subAdded = true;
            // 记住弹幕轨道 id：外挂字幕要挂次轨，主轨不能被抢走
            _danmakuSubId = _player.GetString("sid");
            UpdateDanmakuStatus();
            ApplyOffset();

            StatusText.Text = DescribeDanmaku(path, mismatched);
            UpdateCalibrationText();
        }
        catch (Exception ex)
        {
            ShowError($"弹幕加载失败：{ex.Message}");
        }
    }

    private string DescribeDanmaku(string path, bool mismatched)
    {
        if (_track == null) return Path.GetFileName(path);
        var parts = new List<string> { Path.GetFileName(path), $"{_track.Items.Count} 条" };
        if (_droppedDanmaku > 0) parts.Add($"丢 {_droppedDanmaku}");
        if (_filteredDanmaku > 0) parts.Add($"筛掉 {_filteredDanmaku}");
        if (mismatched) parts.Add("锚点来自另一份弹幕");
        return string.Join(" · ", parts);
    }

    /// <summary>按当前设置和校准重建 ASS，并用 sub-reload 热更新。</summary>
    private void RegenerateAss()
    {
        if (_track == null || _assPath == null) return;

        Func<double, double>? map = _calibration is { IsEmpty: false } ? _calibration.ToLocal : null;
        var duration = MediaDuration;
        var limit = double.IsNaN(duration) || duration <= 0 ? double.MaxValue : duration;

        var built = _settings.Enabled
            ? AssBuilder.WriteToFile(_track, _assPath, _settings, map, limit)
            : new AssBuildResult(string.Empty, 0, _track.Items.Count);

        if (!_settings.Enabled)
        {
            File.WriteAllText(_assPath, EmptyAss());
        }

        _droppedDanmaku = built.Dropped;
        _filteredDanmaku = built.Filtered;

        if (_player == null || !_subAdded) return;

        if (!_player.SubReload())
        {
            var secondary = _player.GetString("secondary-sid");
            _player.Command("sub-remove");
            _player.Command("sub-add", _assPath, "select");
            _player.SetProperty("sub-visibility", "yes");
            _danmakuSubId = _player.GetString("sid");
            // 次字幕轨（用户外挂字幕）在重加弹幕后恢复
            if (int.TryParse(secondary, out var secondaryId)) _player.SetSubtitleTrack(secondaryId, secondary: true);
        }
    }

    private static string EmptyAss() =>
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    public void ApplyCalibration(Calibration calibration, bool persist = true)
    {
        _calibration = calibration;
        calibration.DanmakuSource ??= _track?.Source;

        if (persist && _currentFile != null) CalibrationStore.Save(_currentFile, calibration);

        RegenerateAss();
        UpdateCalibrationText();
        if (_danmakuFile != null) StatusText.Text = DescribeDanmaku(_danmakuFile, false);
    }

    public void ApplySettings(DanmakuSettings settings, bool persist = true)
    {
        _settings = settings;
        if (persist) DanmakuSettingsStore.Save(settings);
        SyncDanmakuToggle();
        RegenerateAss();
        if (_danmakuFile != null) StatusText.Text = DescribeDanmaku(_danmakuFile, false);
    }

    private void SyncDanmakuToggle()
    {
        _updatingUi = true;
        DanmakuToggle.IsChecked = _settings.Enabled;
        _updatingUi = false;
    }

    private void UpdateCalibrationText()
    {
        CalibrationText.Text = _calibration is { IsEmpty: false }
            ? $"校准 {_calibration.Anchors.Count} 点"
            : "未校准";
    }

    private void UpdateDanmakuStatus()
    {
        if (_player == null) return;
        _updatingUi = true;
        DanmakuToggle.IsChecked = _settings.Enabled;
        _updatingUi = false;
    }

    private void ApplyOffset()
    {
        _player?.SetProperty("sub-delay", _offsetSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        UpdateOffsetText();
    }

    private void UpdateOffsetText() => StatusText.ToolTip = $"弹幕偏移 {_offsetSeconds:+0.0;-0.0;0.0} 秒";

    private void UpdateStatus()
    {
        if (_player == null) return;

        var pos = _player.GetDouble("time-pos");
        var dur = _player.GetDouble("duration");
        var paused = _player.GetFlag("pause");

        PlayButton.Content = paused ? "\uE768" : "\uE769";
        UpdateSleepState(!paused && !double.IsNaN(pos) && pos > 0);

        // 文件打不开时 mpv 会回到 idle 状态：给个明确的红色提示，而不是一直黑屏
        if (_currentFile != null && double.IsNaN(_player.GetDouble("duration")) && _player.GetFlag("idle-active"))
        {
            if (++_idleTicks == 8) ShowError($"无法播放这个文件：{Path.GetFileName(_currentFile)}（格式不支持或文件损坏）");
        }
        else
        {
            _idleTicks = 0;
        }

        if (!_seeking && !double.IsNaN(pos))
        {
            _updatingUi = true;
            SeekSlider.Maximum = double.IsNaN(dur) || dur <= 0 ? 1 : dur;
            SeekSlider.Value = Math.Clamp(pos, 0, SeekSlider.Maximum);
            TimeText.Text = Format(pos);
            DurationText.Text = Format(dur);
            _updatingUi = false;
        }

        if (++_tickCount % 8 == 0) RefreshTracks();

        if (_pendingSeek is { } target && !double.IsNaN(dur) && dur > 0)
        {
            _pendingSeek = null;
            if (target >= dur - 2)
            {
                // 距片尾不足 2 秒的续播记录直接丢掉：mac 版同样从头播放
                _resumeTarget = null;
                StatusText.Text = "上次停在片尾附近，从头播放";
            }
            else
            {
                _player.SeekAbsolute(target);
                StatusText.Text = $"从 {Format(target)} 继续";
            }
        }

        if (_resumeTarget is { } resumeTarget && pos >= resumeTarget - 1) _resumeTarget = null;

        if (!_durationReported && _currentFile != null && !double.IsNaN(dur) && dur > 0)
        {
            _durationReported = true;
            DurationReported?.Invoke(_currentFile, dur);
        }

        if (_currentFile != null && !_watchedThisSession && !double.IsNaN(dur) && dur > 0 && pos / dur > 0.95)
        {
            _watchedThisSession = true;
            _resume.MarkWatched(_currentFile);
            StatusText.Text = "已看完";
            _lastResumeSave = DateTime.UtcNow;
        }
        else if (_currentFile != null && !_watchedThisSession && _resumeTarget == null && !double.IsNaN(pos) && pos > 0 &&
                 (DateTime.UtcNow - _lastResumeSave).TotalSeconds >= 5)
        {
            _resume.Save(_currentFile, pos);
            _lastResumeSave = DateTime.UtcNow;
        }
    }

    /// <summary>播放中阻止系统休眠和锁屏计时（对齐 mac 版的 beginActivity）。</summary>
    private void UpdateSleepState(bool playing)
    {
        if (playing == _keepAwake) return;
        _keepAwake = playing;
        SetThreadExecutionState(playing
            ? EsContinuous | EsSystemRequired | EsDisplayRequired
            : EsContinuous);
    }

    private void RefreshTracks()
    {
        if (_player == null) return;

        var tracks = _player.GetTracks();
        var signature = string.Join("|", tracks.Select(t => $"{t.Id}:{t.Type}:{t.Label}:{t.Selected}"));
        if (signature == _trackSignature) return;
        _trackSignature = signature;

        _updatingUi = true;
        try
        {
            var audioSelected = tracks.FirstOrDefault(t => t.Type == "audio" && t.Selected)?.Id;
            AudioBox.Items.Clear();
            foreach (var track in tracks.Where(t => t.Type == "audio"))
            {
                var item = new ComboBoxItem { Content = track.Label, Tag = track.Id };
                AudioBox.Items.Add(item);
                if (track.Id == audioSelected) AudioBox.SelectedItem = item;
            }

            var secondaryId = int.TryParse(_player.GetString("secondary-sid"), out var parsedSecondary)
                ? parsedSecondary
                : (int?)null;
            var danmakuId = int.TryParse(_danmakuSubId, out var parsedDanmaku) ? parsedDanmaku : (int?)null;

            // 同一目录同名外挂字幕由 mpv 的 sub-auto=fuzzy 发现；自动挂到次字幕轨，
            // 主轨继续留给弹幕。只做一次，之后尊重用户手动选择。
            if (!_autoSubtitleApplied)
            {
                // 排除所有由我们生成的弹幕轨（外挂文件名就是 _assPath），否则它会被误当外挂字幕挂到次轨
                var external = tracks.FirstOrDefault(t => t.Type == "sub" && t.External && t.Id != danmakuId &&
                                                          !string.Equals(t.ExternalFile, _assPath, StringComparison.OrdinalIgnoreCase));
                if (external != null)
                {
                    _player.SetSubtitleTrack(external.Id, secondary: true);
                    secondaryId = external.Id;
                    _autoSubtitleApplied = true;
                    StatusText.Text = $"已自动挂载外挂字幕：{external.Label}";
                }
            }

            SubtitleBox.Items.Clear();
            var off = new ComboBoxItem { Content = "关闭字幕", Tag = null };
            SubtitleBox.Items.Add(off);
            foreach (var track in tracks.Where(t => t.Type == "sub" && t.Id != danmakuId))
            {
                var item = new ComboBoxItem { Content = track.Label, Tag = track.Id };
                SubtitleBox.Items.Add(item);
                if (track.Id == secondaryId) SubtitleBox.SelectedItem = item;
            }

            if (audioSelected == null && AudioBox.Items.Count > 0) AudioBox.SelectedIndex = 0;
            if (SubtitleBox.SelectedItem == null) SubtitleBox.SelectedIndex = 0;
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private static string Format(double seconds) =>
        double.IsNaN(seconds) || seconds < 0
            ? "0:00"
            : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择视频",
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.ts|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true) Open(dialog.FileName);
    }

    private void OnLoadDanmakuClick(object sender, RoutedEventArgs e)
    {
        OpenDanmakuFileDialog();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _player?.TogglePause();

    private void OnBackClick(object sender, RoutedEventArgs e) => _player?.SeekRelative(-5);

    private void OnForwardClick(object sender, RoutedEventArgs e) => _player?.SeekRelative(5);

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    public bool IsFullscreen => _isFullscreen;

    /// <summary>自检用：切一次全屏，方便核对窗口是否真的铺满显示器。</summary>
    public void ToggleFullscreenForTest() => ToggleFullscreen();

    /// <summary>真全屏：无边框铺满整个显示器（含任务栏区域），Esc / F 退出并恢复原状态。</summary>
    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        _savedStyle = WindowStyle;
        _savedState = WindowState;
        _savedResize = ResizeMode;
        _savedBounds = new Rect(Left, Top, Width, Height);

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (GetMonitorInfo(monitor, ref info))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = info.Monitor.Left / dpi.DpiScaleX;
            Top = info.Monitor.Top / dpi.DpiScaleY;
            Width = (info.Monitor.Right - info.Monitor.Left) / dpi.DpiScaleX;
            Height = (info.Monitor.Bottom - info.Monitor.Top) / dpi.DpiScaleY;
            _isFullscreen = true;
        }
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        WindowStyle = _savedStyle;
        ResizeMode = _savedResize;
        WindowState = WindowState.Normal;
        Left = _savedBounds.Left;
        Top = _savedBounds.Top;
        Width = _savedBounds.Width;
        Height = _savedBounds.Height;
        WindowState = _savedState;
    }

    private void OnDragOverFile(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFile(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var file = paths.FirstOrDefault(MediaExtensions.IsVideo) ?? paths.FirstOrDefault(File.Exists);
        if (file == null) return;

        e.Handled = true;
        Open(file);
    }

    private void OnSeekDragStart(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void OnSeekDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        _player?.SeekAbsolute(SeekSlider.Value);
    }

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || !IsInitialized) return;
        TimeText.Text = Format(e.NewValue);
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _player == null) return;
        if (SpeedBox.SelectedItem is ComboBoxItem { Tag: double speed }) _player.SetSpeed(speed);
    }

    private void OnAudioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _player == null) return;
        if (AudioBox.SelectedItem is ComboBoxItem { Tag: int id }) _player.SetAudioTrack(id);
    }

    private void OnSubtitleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _player == null) return;
        var id = SubtitleBox.SelectedItem is ComboBoxItem { Tag: int value } ? value : (int?)null;
        // 弹幕占着主字幕轨，所以用户字幕一律走次字幕轨，两者可以同时显示
        _player.SetSubtitleTrack(id, secondary: true);
    }

    private void OnSubtitleMenuClick(object sender, RoutedEventArgs e)
    {
        if (SubtitleMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = SubtitleMenuButton;
        menu.IsOpen = true;
    }

    private void OnAddSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择字幕文件",
            Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        StatusText.Text = AttachExternalSubtitle(dialog.FileName)
            ? $"外挂字幕：{Path.GetFileName(dialog.FileName)}"
            : "字幕加载失败";
    }

    /// <summary>外挂字幕挂到次字幕轨；主字幕轨继续留给弹幕。</summary>
    private bool AttachExternalSubtitle(string file)
    {
        if (_player == null) return false;

        var id = _player.AddSubtitle(file);
        if (id is not int trackId) return false;

        if (int.TryParse(_danmakuSubId, out var primary)) _player.SetSubtitleTrack(primary, secondary: false);
        _player.SetSubtitleTrack(trackId, secondary: true);
        _trackSignature = string.Empty;
        return true;
    }

    private void OnSubtitleEarlierClick(object sender, RoutedEventArgs e) => NudgeSubtitle(-0.1);

    private void OnSubtitleLaterClick(object sender, RoutedEventArgs e) => NudgeSubtitle(0.1);

    private void NudgeSubtitle(double delta)
    {
        if (_player == null) return;
        _player.NudgeSecondarySubtitleDelay(delta);
        StatusText.Text = $"外挂字幕延迟 {_player.GetSecondarySubtitleDelay():+0.0;-0.0;0.0} 秒";
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;
        if (VolumeSlider.Value > 0)
        {
            _volumeBeforeMute = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else
        {
            VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 80;
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi || _player == null) return;
        _player.SetVolume(e.NewValue);
    }

    private void OnDanmakuSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new DanmakuSettingsWindow(this) { Owner = this };
        window.ShowDialog();
    }

    private void OnToggleDanmakuClick(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        var settings = _settings.Clone();
        settings.Enabled = DanmakuToggle.IsChecked == true;
        ApplySettings(settings);
    }

    private sealed record BiliMeta(string? Bvid, long? Cid, string? Title);

    private BiliMeta? ReadBiliMeta()
    {
        if (_currentFile == null) return null;
        var meta = Path.ChangeExtension(_currentFile, ".bili.json");
        if (!File.Exists(meta)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var root = doc.RootElement;
            long? cid = root.TryGetProperty("Cid", out var cidElement) && cidElement.TryGetInt64(out var value) ? value : null;
            var bvid = root.TryGetProperty("Bvid", out var bvidElement) ? bvidElement.GetString() : null;
            var title = root.TryGetProperty("Title", out var titleElement) ? titleElement.GetString() : null;
            return new BiliMeta(bvid, cid, title);
        }
        catch
        {
            return null;
        }
    }

    private void OnCacheMenuClick(object sender, RoutedEventArgs e)
    {
        if (CacheMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = CacheMenuButton;
        menu.IsOpen = true;
    }

    private void OnRefreshBiliClick(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;

        var meta = ReadBiliMeta();
        if (meta?.Cid is not long cid)
        {
            StatusText.Text = "当前视频没有 B 站来源记录，先用「B 站链接…」拉一次";
            return;
        }

        try
        {
            using var client = new BiliClient();
            var path = client.FetchDanmaku(cid, refresh: true);
            LoadDanmaku(path);
            if (_currentFile != null) File.Copy(path, Path.ChangeExtension(_currentFile, ".xml"), overwrite: true);
            StatusText.Text = $"已刷新 B 站弹幕（cid {cid}）：{_track?.Items.Count ?? 0} 条";
        }
        catch (Exception ex)
        {
            ShowError($"刷新失败：{ex.Message}");
        }
    }

    private void OnClearDanmakuCacheClick(object sender, RoutedEventArgs e)
    {
        if (_player == null || _currentFile == null) return;

        try
        {
            _player.Command("sub-remove");
            _subAdded = false;
            _track = null;
            _danmakuSubId = null;

            var sidecar = Path.ChangeExtension(_currentFile, ".xml");
            if (File.Exists(sidecar)) File.Delete(sidecar);

            if (ReadBiliMeta()?.Cid is long cid)
            {
                var cache = Path.Combine(BiliClient.CacheDirectory, $"{cid}.xml");
                if (File.Exists(cache)) File.Delete(cache);
            }

            var metaPath = Path.ChangeExtension(_currentFile, ".bili.json");
            if (File.Exists(metaPath)) File.Delete(metaPath);

            DanmakuCleared?.Invoke(_currentFile);
            StatusText.Text = "已清除弹幕缓存（视频文件未动）";
        }
        catch (Exception ex)
        {
            ShowError($"清除失败：{ex.Message}");
        }
    }

    private void OnPlaylistMenuClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = PlaylistButton;
        menu.IsOpen = true;
    }

    private void OnPlaylistMenuOpened(object sender, RoutedEventArgs e)
    {
        PlaylistMenu.Items.Clear();

        var items = PlaylistProvider?.Invoke() ?? Array.Empty<MediaItem>();
        if (items.Count == 0)
        {
            PlaylistMenu.Items.Add(new MenuItem { Header = "媒体库里没有条目", IsEnabled = false });
            return;
        }

        foreach (var item in items)
        {
            var label = item.EpisodeLabel.Length > 0 ? $"{item.Title} · {item.EpisodeLabel}" : item.Title;
            var entry = new MenuItem
            {
                Header = label,
                IsChecked = string.Equals(item.Path, _currentFile, StringComparison.OrdinalIgnoreCase)
            };
            entry.Click += (_, _) => PlayRequested?.Invoke(item);
            PlaylistMenu.Items.Add(entry);
        }
    }

    private void OnBiliClick(object sender, RoutedEventArgs e)
    {        if (_player == null)
        {
            StatusText.Text = "先打开一个视频，再拉弹幕";
            return;
        }

        var window = new BiliWindow(this, searchTitle: SearchTitleForBili()) { Owner = this };
        window.ShowDialog();
    }

    private string? SearchTitleForBili() =>
        _currentFile == null ? null : Path.GetFileNameWithoutExtension(_currentFile);

    /// <summary>B 站弹幕载入后把 XML 存成视频边车，下次打开自动带弹幕。</summary>
    public void LoadBiliDanmaku(string xmlPath, BiliTarget target)
    {
        // 换源会让旧锚点失去意义，先问一句
        if (_currentFile != null && _calibration is { IsEmpty: false } &&
            !string.Equals(_calibration.DanmakuSource, Path.GetFileName(xmlPath), StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(this,
                "更换弹幕来源会重置现有校准锚点，继续吗？",
                "更换弹幕来源", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            _calibration = null;
            CalibrationStore.Save(_currentFile, new Calibration { OffsetSeconds = _offsetSeconds });
        }

        LoadDanmaku(xmlPath);
        if (_currentFile == null) return;

        try
        {
            var sidecar = Path.ChangeExtension(_currentFile, ".xml");
            if (!string.Equals(Path.GetFullPath(sidecar), Path.GetFullPath(xmlPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(xmlPath, sidecar, overwrite: true);
            _danmakuFile = sidecar;

            var meta = Path.ChangeExtension(_currentFile, ".bili.json");
            File.WriteAllText(meta, JsonSerializer.Serialize(new
            {
                target.Bvid,
                target.Cid,
                target.Title,
                target.Page,
                target.EpId,
                target.SeasonId,
                SavedUtc = DateTime.UtcNow.ToString("o")
            }, new JsonSerializerOptions { WriteIndented = true }));

            StatusText.Text = $"{target.Title} · {_track?.Items.Count ?? 0} 条（已存边车）";
        }
        catch (Exception ex)
        {
            ShowError($"弹幕已加载，但写边车文件失败：{ex.Message}");
        }
    }

    private void OnCalibrateClick(object sender, RoutedEventArgs e)
    {
        if (_track == null)
        {
            ShowError("先加载弹幕再校准");
            return;
        }

        var window = new DanmakuSettingsWindow(this, initialTab: 1) { Owner = this };
        window.ShowDialog();
        UpdateCalibrationText();
    }

    private void ChangeOffset(double delta) => SetOffset(_offsetSeconds + delta);

    /// <summary>顶部红色错误横幅，对齐 mac 播放器的 error banner。</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
        StatusText.Text = message;
        Diagnostics.Log("error " + message);
    }

    private void ClearError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    private void OnCloseErrorClick(object sender, RoutedEventArgs e) => ClearError();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_player == null) return;

        // 输入框和下拉框有原生键盘优先级：把空格/方向键留给它们，不要触发播放控制
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        if (PlaylistMenu.IsOpen || (CacheMenuButton.ContextMenu?.IsOpen ?? false)) return;

        switch (e.Key)
        {
            case Key.Space:
                _player.TogglePause();
                e.Handled = true;
                break;
            case Key.Left:
                _player.SeekRelative(-5);
                e.Handled = true;
                break;
            case Key.Right:
                _player.SeekRelative(5);
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                ChangeOffset(-0.1);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                ChangeOffset(0.1);
                e.Handled = true;
                break;
            case Key.D:
                DanmakuToggle.IsChecked = !(DanmakuToggle.IsChecked == true);
                OnToggleDanmakuClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.C:
                OnCalibrateClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullscreen)
                {
                    ExitFullscreen();
                    e.Handled = true;
                }
                break;
            case Key.O:
                OnOpenClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    /// <summary>系统即将休眠：暂停并保存进度，避免唤醒后时间轴跳变（对齐 mac 版）。</summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Suspend || _player == null) return;

        try
        {
            _player.Pause();
            SaveResumeNow();
            Diagnostics.Log("system suspend: paused and saved");
        }
        catch
        {
            // 休眠通知处理失败不能拦住系统
        }
    }

    private void SaveResumeNow()
    {
        if (_currentFile == null || _player == null || _watchedThisSession) return;
        var pos = _player.GetDouble("time-pos");
        if (!double.IsNaN(pos) && pos > 0) _resume.Save(_currentFile, pos);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SaveResumeNow();
        _player?.Dispose();
        _player = null;
        if (_keepAwake)
        {
            _keepAwake = false;
            SetThreadExecutionState(EsContinuous);
        }
    }

    /// <summary>自检：加载视频（可选弹幕/校准/设置），播到指定位置后采集 mpv 实际属性与字幕文本并写 JSON 报告。</summary>
    public async void RunSelfTest(
        string file,
        string reportPath,
        string? danmakuPath = null,
        double offsetSeconds = 0,
        string? calibrationFile = null,
        double? atSeconds = null,
        string? expectedText = null,
        string? lateCalibrationFile = null,
        bool probeUi = false,
        string? settingsFile = null,
        string? absentText = null,
        string? subtitleFile = null,
        string? biliCid = null,
        string? biliTitle = null,
        bool goFullscreen = false)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var reportDir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(reportDir)) Directory.CreateDirectory(reportDir);
        var framePath = Path.Combine(logDir, "selftest-frame.png");
        var report = new Dictionary<string, object?>();

        try
        {
            _suppressResume = true;
            Open(file);

            if (!string.IsNullOrWhiteSpace(settingsFile))
            {
                ApplySettings(DanmakuSettingsStore.Load(settingsFile), persist: false);
            }

            if (!string.IsNullOrWhiteSpace(danmakuPath)) LoadDanmaku(danmakuPath);

            if (!string.IsNullOrWhiteSpace(calibrationFile))
            {
                var loaded = CalibrationStore.LoadFlexible(calibrationFile, _currentFile)
                             ?? throw new InvalidOperationException($"读不出校准文件：{calibrationFile}");
                ApplyCalibration(loaded, persist: false);
            }

            if (Math.Abs(offsetSeconds) > 0.0001)
            {
                _offsetSeconds = offsetSeconds;
                ApplyOffset();
            }

            if (!string.IsNullOrWhiteSpace(subtitleFile) && _player != null)
            {
                if (AttachExternalSubtitle(subtitleFile)) _player.NudgeSecondarySubtitleDelay(0.2);
            }

            await WaitForDuration(TimeSpan.FromSeconds(15));

            if (goFullscreen)
            {
                ToggleFullscreenForTest();
                await Task.Delay(TimeSpan.FromSeconds(0.8));
            }

            bool? calibrationUiLoaded = null;
            bool? settingsUiLoaded = null;
            bool? biliUiLoaded = null;
            bool? editUiLoaded = null;
            bool? libraryUiLoaded = null;
            bool? appSettingsUiLoaded = null;
            string? uiProbeError = null;
            if (probeUi && _track != null)
            {
                var calibrationProbe = await ProbeWindowAsync(() => new CalibrationWindow(this));
                var settingsProbe = await ProbeWindowAsync(() => new DanmakuSettingsWindow(this));
                var biliProbe = await ProbeWindowAsync(() => new BiliWindow(this));
                var editProbe = await ProbeWindowAsync(() => new EditMediaWindow(new MediaItem { Title = "探针条目", Path = file }));
                var libraryProbe = await ProbeWindowAsync(() => new LibraryWindow());
                var appSettingsProbe = await ProbeWindowAsync(() => new AppSettingsWindow(_ => { }));
                calibrationUiLoaded = calibrationProbe.Loaded;
                settingsUiLoaded = settingsProbe.Loaded;
                biliUiLoaded = biliProbe.Loaded;
                editUiLoaded = editProbe.Loaded;
                libraryUiLoaded = libraryProbe.Loaded;
                appSettingsUiLoaded = appSettingsProbe.Loaded;
                uiProbeError = string.Join(" ; ", new[] { calibrationProbe.Error, settingsProbe.Error, biliProbe.Error, editProbe.Error, libraryProbe.Error, appSettingsProbe.Error }.Where(e => e != null));
            }

            if (atSeconds is { } at)
            {
                _player?.SeekAbsolute(at);
                await Task.Delay(TimeSpan.FromSeconds(1.2));
                _player?.Pause();
                await Task.Delay(TimeSpan.FromSeconds(1.0));
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
            }

            var p = _player ?? throw new InvalidOperationException("MpvPlayer 未创建");

            string? subTextBeforeLate = null;
            if (!string.IsNullOrWhiteSpace(lateCalibrationFile))
            {
                subTextBeforeLate = p.GetSubText() ?? string.Empty;
                var late = CalibrationStore.LoadFlexible(lateCalibrationFile, _currentFile)
                           ?? throw new InvalidOperationException($"读不出校准文件：{lateCalibrationFile}");
                ApplyCalibration(late, persist: false);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            }

            var withSubtitles = _track != null;
            var shot = p.Screenshot(framePath, withSubtitles);
            var subText = p.GetSubText() ?? string.Empty;
            await Task.Delay(TimeSpan.FromSeconds(1));

            var width = p.GetInt("video-params/w");
            var height = p.GetInt("video-params/h");
            var samplerate = p.GetInt("audio-out-params/samplerate");
            var vo = p.GetString("vo-configured");
            var timePos = p.GetDouble("time-pos");
            var subVisible = p.GetString("sub-visibility");
            var subDelayText = p.GetString("sub-delay");

            report["app"] = "DanmakuPlayerWin";
            report["libmpvClientApi"] = MpvPlayer.ClientApiVersion();
            report["file"] = file;
            report["durationSeconds"] = Clean(p.GetDouble("duration"));
            report["timePosSeconds"] = Clean(timePos);
            report["requestedAtSeconds"] = atSeconds;
            report["videoWidth"] = width;
            report["videoHeight"] = height;
            report["hwdecCurrent"] = p.GetString("hwdec-current");
            report["voConfigured"] = vo;
            report["currentAo"] = p.GetString("current-ao");
            report["audioSamplerate"] = samplerate;
            report["mpvLog"] = p.LogPath;
            report["danmakuSource"] = _track?.Source;
            report["biliCid"] = biliCid;
            report["biliTitle"] = biliTitle;
            report["danmakuCount"] = _track?.Items.Count ?? 0;
            report["danmakuDropped"] = _droppedDanmaku;
            report["danmakuFiltered"] = _filteredDanmaku;
            report["settings"] = new
            {
                _settings.Enabled,
                _settings.FontSize,
                _settings.FontName,
                _settings.Bold,
                _settings.Opacity,
                _settings.Area,
                _settings.MaxVisible,
                _settings.ScrollSpeed,
                _settings.Scrolling,
                _settings.Top,
                _settings.Bottom,
                _settings.BlockedWords
            };
            report["calibrationCount"] = _calibration?.Anchors.Count ?? 0;
            report["subVisibility"] = subVisible;
            report["subDelay"] = subDelayText;
            report["subText"] = subText;
            report["subTextBeforeLate"] = subTextBeforeLate;
            report["expectedText"] = expectedText;
            report["absentText"] = absentText;
            report["calibrationUiLoaded"] = calibrationUiLoaded;
            report["settingsUiLoaded"] = settingsUiLoaded;
            report["biliUiLoaded"] = biliUiLoaded;
            report["editUiLoaded"] = editUiLoaded;
            report["libraryUiLoaded"] = libraryUiLoaded;
            report["appSettingsUiLoaded"] = appSettingsUiLoaded;
            var topLevel = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            report["windowGeometry"] = WindowDiagnostics.Snapshot(topLevel);
            report["fullscreen"] = IsFullscreen;
            report["secondarySubtitleDelay"] = Clean(p.GetDouble("secondary-sub-delay"));
            report["secondarySubtitleText"] = p.GetSecondarySubtitleText() ?? string.Empty;
            report["secondarySid"] = p.GetString("secondary-sid");
            report["primarySid"] = p.GetString("sid");
            report["tracks"] = p.GetTracks().Select(t => new { t.Id, t.Type, t.Label, t.Selected }).ToList();
            report["secondarySubtitleRequested"] = !string.IsNullOrWhiteSpace(subtitleFile);
            report["uiProbeError"] = uiProbeError;
            report["screenshotMode"] = withSubtitles ? "subtitles" : "video";            report["screenshotPath"] = framePath;
            report["screenshotBytes"] = File.Exists(framePath) ? new FileInfo(framePath).Length : 0;

            var checks = new Dictionary<string, bool>
            {
                ["videoDecoded"] = width > 0 && height > 0,
                ["videoOutputConfigured"] = vo == "yes",
                ["audioDecoded"] = samplerate > 0,
                ["playing"] = !double.IsNaN(timePos) && timePos > 1.0,
                ["screenshotWritten"] = File.Exists(framePath) && new FileInfo(framePath).Length > 1000,
                ["danmakuOverlayVisible"] = !withSubtitles || subVisible == "yes",
                ["danmakuOffsetApplied"] = !withSubtitles ||
                    (double.TryParse(subDelayText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                     && Math.Abs(d - offsetSeconds) < 0.001),
                ["seekedToRequestedTime"] = atSeconds is not { } want || (Math.Abs(timePos - want) <= 1.5),
                ["expectedSubtitleVisible"] = expectedText == null || subText.Contains(expectedText, StringComparison.Ordinal),
                ["absentSubtitleHidden"] = absentText == null || !subText.Contains(absentText, StringComparison.Ordinal),
                ["lateCalibrationChangedText"] = lateCalibrationFile == null || subText != subTextBeforeLate,
                ["calibrationUiLoaded"] = calibrationUiLoaded != false,
                ["settingsUiLoaded"] = settingsUiLoaded != false,
                ["biliUiLoaded"] = biliUiLoaded != false,
                ["editUiLoaded"] = editUiLoaded != false,
                ["libraryUiLoaded"] = libraryUiLoaded != false,
                ["appSettingsUiLoaded"] = appSettingsUiLoaded != false,
                ["fullscreenApplied"] = !goFullscreen || IsFullscreen,
                ["secondarySubtitleVisible"] = string.IsNullOrWhiteSpace(subtitleFile) ||
                    !string.IsNullOrWhiteSpace(p.GetSecondarySubtitleText())
            };
            report["checks"] = checks;
            var ok = checks.Values.All(v => v);
            report["ok"] = ok;

            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            p.Dispose();
            _player = null;
            Environment.Exit(ok ? 0 : 1);
        }
        catch (Exception ex)
        {
            report["ok"] = false;
            report["error"] = ex.ToString();
            try
            {
                var dir = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 报告写不出去时只能靠退出码 */ }
            Environment.Exit(2);
        }
    }

    private async Task<(bool Loaded, string? Error)> ProbeWindowAsync<T>(Func<T> factory) where T : Window
    {
        T? window = null;
        try
        {
            window = factory();
            window.Show();
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            return (window.IsLoaded, null);
        }
        catch (Exception ex)
        {
            var message = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) message += " | " + ex.InnerException.Message;
            var frame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(frame)) message += " @ " + frame;
            return (false, message);
        }
        finally
        {
            window?.Close();
        }
    }

    private async Task WaitForDuration(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var duration = _player?.GetDouble("duration") ?? double.NaN;
            if (!double.IsNaN(duration) && duration > 0) return;
            await Task.Delay(200);
        }
    }

    private static object? Clean(double value) => double.IsNaN(value) || double.IsInfinity(value) ? null : value;
}
