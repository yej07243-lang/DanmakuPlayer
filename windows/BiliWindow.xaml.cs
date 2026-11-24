using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DanmakuPlayerWin.Bili;
using Microsoft.Win32;

namespace DanmakuPlayerWin;

public partial class BiliWindow : Window
{
    private readonly MainWindow? _owner;
    private readonly string? _mediaPath;
    private readonly Action<BiliTarget, string>? _onLoaded;
    private readonly Action<string>? _onXmlLoaded;
    private readonly string? _searchTitle;
    private readonly int? _season;
    private readonly int? _year;
    private readonly BiliClient _client = new();
    private IReadOnlyList<BiliCandidate> _candidates = Array.Empty<BiliCandidate>();
    private IReadOnlyList<BiliTarget> _targets = Array.Empty<BiliTarget>();
    private int _resolveGeneration;
    private int _loadGeneration;
    private bool _closed;

    public BiliWindow(MainWindow? owner = null, string? mediaPath = null,
        Action<BiliTarget, string>? onLoaded = null, Action<string>? onXmlLoaded = null,
        string? searchTitle = null, int? season = null, int? year = null)
    {
        InitializeComponent();
        _owner = owner;
        _mediaPath = mediaPath;
        _onLoaded = onLoaded;
        _onXmlLoaded = onXmlLoaded;
        _searchTitle = searchTitle;
        _season = season;
        _year = year;

        // 普通投稿搜索默认带入本地标题；官方影视搜索再补季数，和 mac 版一样
        if (!string.IsNullOrWhiteSpace(searchTitle)) SearchBox.Text = searchTitle;

        Closed += (_, _) =>
        {
            _closed = true;
            _client.Dispose();
        };
    }

    private void OnResolveClick(object sender, RoutedEventArgs e)
    {
        var input = LinkBox.Text.Trim();
        if (input.Length == 0)
        {
            StatusText.Text = "先粘贴链接";
            return;
        }

        _ = RunAsync(async () =>
        {
            var targets = await Task.Run(() => _client.Resolve(input));
            _candidates = Array.Empty<BiliCandidate>();
            CandidateList.Items.Clear();
            ShowTargets(targets);
        });
    }

    private void OnLinkChanged(object sender, TextChangedEventArgs e)
    {
        // 改链接后旧候选和旧分集都不再可信
        if (_candidates.Count == 0 && _targets.Count == 0) return;

        _candidates = Array.Empty<BiliCandidate>();
        _targets = Array.Empty<BiliTarget>();
        CandidateList.Items.Clear();
        TargetList.Items.Clear();
        SelectedText.Text = "未选择目标";
        StatusText.Text = string.Empty;
    }

    private void OnSearchVideoClick(object sender, RoutedEventArgs e) => Search(official: false);

    private void OnSearchOfficialClick(object sender, RoutedEventArgs e) => Search(official: true);

    private void Search(bool official)
    {
        var keyword = SearchBox.Text.Trim();
        if (official && !string.IsNullOrWhiteSpace(_searchTitle) &&
            string.Equals(keyword, _searchTitle, StringComparison.Ordinal))
        {
            // 本地标题 + 季数生成「火线 第一季」这类官方影视搜索词
            if (_season is int season) keyword = $"{keyword} 第{season}季";
            else if (_year is int year) keyword = $"{keyword} {year}";
        }

        if (keyword.Length == 0)
        {
            StatusText.Text = "先填搜索词";
            return;
        }

        _ = RunAsync(async () =>
        {
            var candidates = await Task.Run(() => _client.Search(keyword, official));
            _candidates = candidates;
            CandidateList.Items.Clear();
            foreach (var candidate in candidates) CandidateList.Items.Add(candidate.Label);

            if (candidates.Count > 0)
            {
                CandidateList.SelectedIndex = 0;
                StatusText.Foreground = Brushes.LightGreen;
                StatusText.Text = $"搜到 {candidates.Count} 条（官方影视：{(official ? "是" : "否")}），右边选分 P 或单集。";
            }
            else
            {
                StatusText.Foreground = Brushes.Salmon;
                StatusText.Text = "没有搜到结果";
            }
        });
    }

    private void OnCandidateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedIndex < 0 || CandidateList.SelectedIndex >= _candidates.Count) return;

        var candidate = _candidates[CandidateList.SelectedIndex];
        var generation = ++_resolveGeneration;

        _ = RunAsync(async () =>
        {
            var targets = await Task.Run(() => _client.ResolveCandidate(candidate));
            if (generation != _resolveGeneration) return;
            ShowTargets(targets);
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        StatusText.Foreground = Brushes.LightGray;
        StatusText.Text = "请求中…";
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Foreground = Brushes.Salmon;
            StatusText.Text = ex.Message;
        }
    }

    private void ShowTargets(IReadOnlyList<BiliTarget> targets)
    {
        _targets = targets;
        TargetList.Items.Clear();
        foreach (var target in targets) TargetList.Items.Add(target.Label);
        if (targets.Count > 0) TargetList.SelectedIndex = 0;
        else SelectedText.Text = "未选择目标";

        StatusText.Foreground = Brushes.LightGreen;
        StatusText.Text = $"解析到 {targets.Count} 个目标，选一条后点「载入弹幕」。";
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetList.SelectedIndex < 0 || TargetList.SelectedIndex >= _targets.Count)
        {
            SelectedText.Text = "未选择目标";
            return;
        }

        var target = _targets[TargetList.SelectedIndex];
        var duration = target.DurationSeconds > 0 ? TimeSpan.FromSeconds(target.DurationSeconds).ToString(@"h\:mm\:ss") : "时长未知";
        SelectedText.Text = $"{target.Title}   ·   {duration}   ·   cid {target.Cid}";
    }

    private void OnImportXmlClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择弹幕 XML", Filter = "弹幕 XML|*.xml|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;

        if (_owner != null) _owner.ImportLocalDanmaku(dialog.FileName);
        else _onXmlLoaded?.Invoke(dialog.FileName);

        Close();
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedIndex < 0 || TargetList.SelectedIndex >= _targets.Count)
        {
            StatusText.Foreground = Brushes.Salmon;
            StatusText.Text = "先解析并选中一条目标";
            return;
        }

        var target = _targets[TargetList.SelectedIndex];
        var generation = ++_loadGeneration;
        StatusText.Foreground = Brushes.LightGray;
        StatusText.Text = $"正在拉取 cid {target.Cid} 的弹幕…";

        try
        {
            var path = await Task.Run(() => _client.FetchDanmaku(target.Cid));
            // 关窗或又点了另一次载入：迟到的结果不能覆盖当前选择
            if (_closed || generation != _loadGeneration) return;

            if (_owner != null) _owner.LoadBiliDanmaku(path, target);
            else _onLoaded?.Invoke(target, path);

            StatusText.Foreground = Brushes.LightGreen;
            StatusText.Text = $"已载入：{target.Title}";
            Close();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            StatusText.Foreground = Brushes.Salmon;
            StatusText.Text = ex.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
