using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DanmakuPlayerWin.Bili;
using DanmakuPlayerWin.Library;
using DanmakuPlayerWin.Playback;
using Microsoft.Win32;

namespace DanmakuPlayerWin;

public partial class LibraryWindow : Window
{
    private readonly LibraryStore _store;
    private readonly ResumeStore _resume = new();
    private readonly Dictionary<string, MainWindow> _players = new(StringComparer.OrdinalIgnoreCase);
    private List<MediaItem> _all = new();
    private bool _scanning;
    private bool _inspectorManual;

    public LibraryWindow()
    {
        InitializeComponent();
        _store = new LibraryStore();
        Loaded += (_, _) => Reload();
        Activated += (_, _) =>
        {
            Theme.ThemeManager.RefreshSystemTheme();
            Reload();
        };
        Closed += (_, _) => _store.Dispose();
    }

    public void Reload()
    {
        try
        {
            _all = _store.Items().ToList();
            foreach (var item in _all)
            {
                item.Available = File.Exists(item.Path);

                // 播放器里切到本地 XML 或清缓存后会删掉 .bili.json，这里把库里的旧来源清掉
                var sidecar = Path.ChangeExtension(item.Path, ".xml");
                var biliMeta = Path.ChangeExtension(item.Path, ".bili.json");
                var changed = false;
                if (!string.IsNullOrWhiteSpace(item.Bvid) && !File.Exists(biliMeta))
                {
                    item.Bvid = null;
                    item.Cid = null;
                    changed = true;
                }
                if (item.LocalDanmaku != null && !File.Exists(item.LocalDanmaku)) item.LocalDanmaku = null;
                if (File.Exists(sidecar) && item.LocalDanmaku == null)
                {
                    item.LocalDanmaku = sidecar;
                    changed = true;
                }
                if (changed) _store.Save(item);

                var position = _resume.GetPosition(item.Path);
                var watched = item.Watched || _resume.IsWatched(item.Path);
                if (watched)
                {
                    // 已看完：从「继续观看」里消失，再播放时从头开始
                    item.Watched = true;
                    item.Position = 0;
                }
                else if (position is { } saved && saved > 0)
                {
                    item.Position = saved;
                    item.Watched = false;
                }
            }

            ApplyFilter();
            StatusText.Text = $"媒体库 {_all.Count} 项 · 数据库 {_store.DatabasePath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取媒体库失败：{ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();
        var section = (SectionList.SelectedItem as ListBoxItem)?.Content as string ?? "全部影片";

        IEnumerable<MediaItem> query = _all;
        query = section switch
        {
            "电影" => query.Where(i => i.Episode == null),
            "剧集" => query.Where(i => i.Episode != null),
            "继续观看" => query.Where(i => i.Position > 0 && !i.Watched),
            "文件不可用" => query.Where(i => !i.Available),
            _ => query
        };

        if (search.Length > 0)
        {
            query = query.Where(i =>
                i.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(i.Path).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        query = SortBox.SelectedIndex == 1
            ? query.OrderByDescending(i => _resume.GetSavedAt(i.Path) ?? i.Modified)
            : query.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Season ?? 0)
                .ThenBy(i => i.Episode ?? 0);

        var list = query.ToList();
        var view = new System.Windows.Data.ListCollectionView(list);
        var grouped = section == "剧集" && SortBox.SelectedIndex == 0;
        if (grouped)
        {
            // mac 版「剧集」按片名分组，同组内再按季/集排序
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(MediaItem.SeriesGroup)));
        }
        ItemList.ItemsPanel = (ItemsPanelTemplate)FindResource(grouped ? "GroupPanelTemplate" : "WrapPanelTemplate");
        ItemList.ItemsSource = view;
        CountText.Text = $"{list.Count} 项";
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _all.Count == 0
            ? "媒体库是空的：点左上「导入文件夹」，或者把视频 / 文件夹直接拖进来。"
            : "这个分栏下没有匹配的条目。";
    }

    private MediaItem? Selected => ItemList.SelectedItem as MediaItem;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = Selected;
        PlayButton.IsEnabled = item != null;
        EditButton.IsEnabled = item != null;
        BiliButton.IsEnabled = item != null;
        RelocateButton.IsEnabled = item != null;
        RemoveButton.IsEnabled = item != null;

        if (item == null)
        {
            DetailTitle.Text = "未选中";
            DetailMeta.Text = string.Empty;
            DetailSummary.Text = string.Empty;
            DetailPath.Text = string.Empty;
            DetailDanmaku.Text = string.Empty;
            return;
        }

        DetailTitle.Text = item.Title;
        var meta = new List<string>();
        if (item.Year is int year) meta.Add(year.ToString());
        if (item.EpisodeLabel.Length > 0) meta.Add(item.EpisodeLabel);
        if (item.Duration > 0) meta.Add(MediaItem.FormatTime(item.Duration));
        if (item.Watched) meta.Add("已看完");
        meta.Add(item.Available ? "文件可用" : "文件不可用");
        if (item.Bvid != null) meta.Add($"B站 {item.Bvid}");
        DetailMeta.Text = string.Join(" · ", meta);
        DetailSummary.Text = item.Summary;
        DetailPath.Text = item.Path;
        DetailDanmaku.Text = DescribeDanmakuSource(item);
        if (item.Position > 0 && !item.Watched)
        {
            var savedAt = _resume.GetSavedAt(item.Path);
            PlayButton.Content = savedAt is DateTime at
                ? $"继续播放 · {MediaItem.FormatTime(item.Position)}（保存于 {at:MM-dd HH:mm}）"
                : $"继续播放 · {MediaItem.FormatTime(item.Position)}";
        }
        else
        {
            PlayButton.Content = "播放";
        }
    }

    private static string DescribeDanmakuSource(MediaItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Bvid))
            return item.Cid is long cid ? $"弹幕来源：B 站 {item.Bvid} · cid {cid}" : $"弹幕来源：B 站 {item.Bvid}";

        var sidecar = Path.ChangeExtension(item.Path, ".xml");
        if (File.Exists(sidecar)) return $"弹幕：同名 XML 边车（{Path.GetFileName(sidecar)}）";
        return "弹幕：未关联（可点「B 站弹幕…」）";
    }

    private void OnOpenDataDirectoryClick(object sender, RoutedEventArgs e)
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
            StatusText.Text = $"打开数据目录失败：{ex.Message}";
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new AppSettingsWindow(settings =>
        {
            foreach (var player in _players.Values) player.ApplySettings(settings.Clone(), persist: false);
        })
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow { Owner = this };
        window.ShowDialog();
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
            StatusText.Text = $"已导出日志：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        var editing = System.Windows.Input.Keyboard.FocusedElement is TextBox or ComboBox;

        if (ctrl && e.Key == System.Windows.Input.Key.O)
        {
            if (shift) OnImportFolderClick(this, new RoutedEventArgs());
            else OnImportFilesClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == System.Windows.Input.Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (!editing && e.Key == System.Windows.Input.Key.Enter)
        {
            Play(Selected);
            e.Handled = true;
        }
    }

    /// <summary>右键先选中鼠标下的卡片，否则上下文菜单会作用在别的条目上。</summary>
    private void OnItemRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var container = ItemsControl.ContainerFromElement(ItemList, source) as ListBoxItem;
        if (container != null) container.IsSelected = true;
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;

        try
        {
            if (File.Exists(item.Path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(item.Path)}\"");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开资源管理器失败：{ex.Message}";
        }
    }

    private void OnToggleInspectorClick(object sender, RoutedEventArgs e)
    {
        _inspectorManual = !_inspectorManual;
        UpdateInspector();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateInspector();

    /// <summary>窄窗自动收起右侧检查器；用户手动收起后保持收起。</summary>
    private void UpdateInspector()
    {
        var hidden = _inspectorManual || ActualWidth < 1100;
        InspectorColumn.Width = hidden ? new GridLength(0) : new GridLength(330);
        InspectorPanel.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (InspectorToggle != null) InspectorToggle.Content = hidden ? "显示详情" : "收起详情";
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void OnImportFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要导入的媒体文件夹", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        Import(dialog.FolderNames);
    }

    private void OnImportFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择视频文件",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.flv;*.wmv;*.mpg;*.mpeg|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        Import(dialog.FileNames);
    }

    private void OnRescanClick(object sender, RoutedEventArgs e)
    {
        var roots = _store.Roots();
        if (roots.Count == 0)
        {
            StatusText.Text = "还没有导入过文件夹，先「导入文件夹」";
            return;
        }
        Import(roots);
    }

    private async void Import(IEnumerable<string> inputs)
    {
        if (_scanning) return;

        var paths = inputs.ToList();
        if (paths.Count == 0) return;

        _scanning = true;
        StatusText.Text = "扫描中…";

        try
        {
            foreach (var path in paths)
            {
                if (MediaExtensions.IsVideo(path))
                {
                    _store.AddRoot(Path.GetDirectoryName(path) ?? path);
                }
                else if (Directory.Exists(path))
                {
                    _store.AddRoot(path);
                }
            }

            var progress = new Progress<string>(text => StatusText.Text = "扫描中… " + text);
            var scanned = await Task.Run(() => MediaScanner.Scan(paths, progress));
            await Task.Run(() => _store.Merge(scanned));

            Reload();
            StatusText.Text = $"扫描完成：{scanned.Count} 个文件，媒体库 {_all.Count} 项";
            Diagnostics.Log($"scan inputs={paths.Count} files={scanned.Count} total={_all.Count}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            _scanning = false;
        }
    }

    private void OnBiliClick(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;

        var window = new BiliWindow(
            mediaPath: item.Path,
            onLoaded: (target, xmlPath) => AttachBili(item, target, xmlPath),
            onXmlLoaded: xmlPath => AttachXml(item, xmlPath),
            searchTitle: item.Title,
            season: item.Season,
            year: item.Year)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    /// <summary>本地 XML 直接当边车；来源信息清空，避免和旧的 B 站条目混在一起。</summary>
    private void AttachXml(MediaItem item, string xmlPath)
    {
        try
        {
            var sidecar = Path.ChangeExtension(item.Path, ".xml");
            if (!string.Equals(Path.GetFullPath(sidecar), Path.GetFullPath(xmlPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(xmlPath, sidecar, overwrite: true);

            var updated = item.Clone();
            updated.Bvid = null;
            updated.Cid = null;
            updated.LocalDanmaku = sidecar;
            updated.Modified = DateTime.UtcNow;
            _store.Save(updated);

            Reload();
            StatusText.Text = $"已使用本地弹幕：{Path.GetFileName(xmlPath)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导入弹幕失败：{ex.Message}";
        }
    }

    /// <summary>把选中分集的弹幕存成边车文件，并把来源写回媒体库条目。</summary>
    private void AttachBili(MediaItem item, BiliTarget target, string xmlPath)
    {
        try
        {
            var existing = CalibrationStore.Load(item.Path);
            if (existing is { IsEmpty: false } &&
                !string.Equals(existing.DanmakuSource, Path.GetFileName(xmlPath), StringComparison.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(this,
                    "更换弹幕来源会重置这个条目的校准锚点，继续吗？",
                    "更换弹幕来源", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                CalibrationStore.Save(item.Path, new Calibration { OffsetSeconds = existing.OffsetSeconds });
            }

            var sidecar = Path.ChangeExtension(item.Path, ".xml");
            if (!string.Equals(Path.GetFullPath(sidecar), Path.GetFullPath(xmlPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(xmlPath, sidecar, overwrite: true);

            File.WriteAllText(Path.ChangeExtension(item.Path, ".bili.json"), JsonSerializer.Serialize(new
            {
                target.Bvid,
                target.Cid,
                target.Title,
                target.Page,
                target.EpId,
                target.SeasonId,
                SavedUtc = DateTime.UtcNow.ToString("o")
            }, new JsonSerializerOptions { WriteIndented = true }));

            var updated = item.Clone();
            updated.Bvid = target.Bvid;
            updated.Cid = target.Cid;
            updated.LocalDanmaku = sidecar;
            updated.Modified = DateTime.UtcNow;
            _store.Save(updated);

            Reload();
            StatusText.Text = $"已关联 B 站弹幕：{target.Title}（cid {target.Cid}）";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"关联失败：{ex.Message}";
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;

        var dialog = new EditMediaWindow(item) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result == null) return;

        try
        {
            _store.Save(dialog.Result);
            if (dialog.Result.Watched && !item.Watched) _resume.MarkWatched(dialog.Result.Path);
            else if (!dialog.Result.Watched && item.Watched) _resume.MarkUnwatched(dialog.Result.Path);
            Reload();
            StatusText.Text = $"已保存：{dialog.Result.Title}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        Import(paths);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => Play(Selected);

    private void OnItemDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Play(Selected);

    private void Play(MediaItem? item)
    {
        if (item == null) return;

        if (!File.Exists(item.Path))
        {
            StatusText.Text = $"{item.Title} 的文件不在了，请选择新位置";
            OnRelocateClick(this, new RoutedEventArgs());
            return;
        }

        if (_players.TryGetValue(item.Path, out var existing))
        {
            existing.Activate();
            return;
        }

        // mac 版同一时间只有一个播放窗口：切到另一部影片时先关掉旧的（关窗会保存进度）
        foreach (var key in _players.Keys.Where(k => !string.Equals(k, item.Path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_players.TryGetValue(key, out var old)) old.Close();
        }
        _players.Clear();

        var player = new MainWindow();
        player.PlaylistProvider = () => ItemList.Items.Cast<MediaItem>().ToList();
        player.PlayRequested = other => Play(other);
        player.DanmakuCleared = path => ClearDanmakuAssociation(path);
        player.DurationReported = (path, duration) => UpdateDuration(path, duration);
        player.Closed += (_, _) =>
        {
            _players.Remove(item.Path);
            Reload();
        };
        _players[item.Path] = player;
        player.RequestOpen(item.Path, item.Title);
        player.Show();
        StatusText.Text = $"正在播放：{item.Title}";
        Diagnostics.Log($"play title={item.Title} path={item.Path}");
    }

    /// <summary>播放器拿到片长后补进媒体库（扫描时 ffprobe 和 libmpv 都没读到时兜底）。</summary>
    private void UpdateDuration(string path, double duration)
    {
        if (duration <= 0) return;
        try
        {
            var item = _store.FindByPath(path);
            if (item == null || item.Duration > 0) return;

            var updated = item.Clone();
            updated.Duration = duration;
            _store.Save(updated);
            Reload();
            StatusText.Text = $"已补上片长：{updated.Title}（{MediaItem.FormatTime(duration)}）";
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"duration update failed path={path}: {ex.Message}");
        }
    }

    /// <summary>播放器里清掉弹幕后，把媒体库条目上的 B 站来源和本地关联一并清掉。</summary>
    private void ClearDanmakuAssociation(string path)
    {
        try
        {
            var item = _store.FindByPath(path);
            if (item == null) return;

            var updated = item.Clone();
            updated.Bvid = null;
            updated.Cid = null;
            updated.LocalDanmaku = null;
            updated.Modified = DateTime.UtcNow;
            _store.Save(updated);

            Reload();
            StatusText.Text = "已清除弹幕来源与缓存";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清除弹幕来源失败：{ex.Message}";
        }
    }

    private void OnRelocateClick(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;

        var dialog = new OpenFileDialog
        {
            Title = $"重新定位「{item.Title}」",
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.flv;*.wmv;*.mpg;*.mpeg|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var oldPath = item.Path;
            var newPath = Path.GetFullPath(dialog.FileName);

            // 重新定位沿用原记录及其观看进度、弹幕关联和校准
            _resume.Move(oldPath, newPath);
            CalibrationStore.Move(oldPath, newPath);
            Relocation.MigrateSidecars(oldPath, newPath);

            var relocated = item.Clone();
            relocated.Path = newPath;
            relocated.Available = true;
            if (File.Exists(Path.ChangeExtension(newPath, ".xml")))
                relocated.LocalDanmaku = Path.ChangeExtension(newPath, ".xml");
            relocated.Modified = DateTime.UtcNow;

            var duplicate = _store.FindByPath(newPath);
            if (duplicate != null && duplicate.Id != item.Id) _store.Remove(duplicate.Id);
            _store.Remove(item.Id);
            _store.Save(relocated);
            Reload();
            StatusText.Text = $"已重新定位：{relocated.Title}（观看进度和弹幕关联已沿用）";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"重新定位失败：{ex.Message}";
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;

        var answer = MessageBox.Show(this,
            $"从媒体库移除「{item.Title}」？\n原始视频文件会保留。",
            "移除确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        _store.Remove(item.Id);
        Reload();
        StatusText.Text = $"已从媒体库移除：{item.Title}";
    }
}
