using System.Windows;
using DanmakuPlayerWin.Playback;

namespace DanmakuPlayerWin;

public partial class CalibrationWindow : Window
{
    private readonly MainWindow _owner;
    private readonly Calibration _working;

    public CalibrationWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        _working = owner.Calibration is { } existing
            ? new Calibration(existing.DanmakuSource, existing.Anchors)
            : new Calibration();

        Refresh();
        Loaded += (_, _) => PrefillBiliTime();
    }

    private void PrefillBiliTime()
    {
        var local = _owner.CurrentLocalTime;
        if (double.IsNaN(local)) return;
        BiliInput.Text = CalibrationStore.FormatTime(_working.ToBili(local));
        UpdatePlayhead(local);
    }

    private void UpdatePlayhead(double local)
    {
        if (double.IsNaN(local))
        {
            PlayheadText.Text = "当前位置：未加载";
            return;
        }

        var offset = _working.OffsetAt(local);
        PlayheadText.Text = $"当前位置：本地 {CalibrationStore.FormatTime(local)} · 本段偏移 {offset:+0.0;-0.0;0.0}s · " +
                            $"按现有锚点对应 B 站 {CalibrationStore.FormatTime(_working.ToBili(local))}";
    }

    private void Refresh()
    {
        AnchorList.Items.Clear();
        for (var i = 0; i < _working.Anchors.Count; i++)
        {
            var anchor = _working.Anchors[i];
            var offset = anchor.Local - anchor.Bili;
            AnchorList.Items.Add(
                $"{i + 1:00}  本地 {CalibrationStore.FormatTime(anchor.Local)}  ↔  B站 {CalibrationStore.FormatTime(anchor.Bili)}   （偏移 {offset:+0.0;-0.0;0.0}s）");
        }

        UpdatePlayhead(_owner.CurrentLocalTime);
        ErrorText.Text = _working.IsEmpty ? "还没有锚点：弹幕按 B 站时间轴原样播放。" : string.Empty;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var local = _owner.CurrentLocalTime;
        if (double.IsNaN(local) || local < 0)
        {
            ErrorText.Text = "还没有可用的播放位置，先加载视频。";
            return;
        }

        if (!CalibrationStore.TryParseTime(BiliInput.Text, out var bili))
        {
            ErrorText.Text = "B 站时间格式不对，用 mm:ss.s 或 hh:mm:ss。";
            return;
        }

        var snapshot = new Calibration(_working.DanmakuSource, _working.Anchors);
        _working.Add(new Anchor(Math.Round(local, 2), Math.Round(bili, 2)));

        if (!_working.TryValidate(out var error))
        {
            _working.Clear();
            foreach (var anchor in snapshot.Anchors) _working.Add(anchor);
            ErrorText.Text = error;
            return;
        }

        ErrorText.Text = string.Empty;
        _owner.ApplyCalibration(_working);
        Refresh();
        PrefillBiliTime();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (AnchorList.SelectedIndex < 0)
        {
            ErrorText.Text = "先选中一条锚点。";
            return;
        }

        _working.RemoveAt(AnchorList.SelectedIndex);
        _owner.ApplyCalibration(_working);
        Refresh();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _working.Clear();
        _owner.ApplyCalibration(_working);
        Refresh();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
