using System.Globalization;
using System.IO;
using System.Windows;
using DanmakuPlayerWin.Library;
using Microsoft.Win32;

namespace DanmakuPlayerWin;

public partial class EditMediaWindow : Window
{
    private readonly MediaItem _item;
    private string? _artwork;

    public EditMediaWindow(MediaItem item)
    {
        InitializeComponent();
        _item = item;

        TitleBox.Text = item.Title;
        YearBox.Text = item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SeasonBox.Text = item.Season?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        EpisodeBox.Text = item.Episode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SummaryBox.Text = item.Summary;
        WatchedCheck.IsChecked = item.Watched;
        PathText.Text = item.Path;
        _artwork = item.Artwork;
        ArtworkText.Text = _artwork == null ? "未选择" : Path.GetFileName(_artwork);
    }

    /// <summary>保存后的条目，调用方负责写库。</summary>
    public MediaItem? Result { get; private set; }

    private void OnPickArtworkClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择海报",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.heic|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        _artwork = dialog.FileName;
        ArtworkText.Text = Path.GetFileName(_artwork);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ErrorText.Text = "标题不能为空";
            return;
        }

        var item = _item.Clone();
        item.Title = title;
        item.Year = ParseOptional(YearBox.Text);
        item.Season = ParseOptional(SeasonBox.Text);
        item.Episode = ParseOptional(EpisodeBox.Text);
        item.Summary = SummaryBox.Text.Trim();
        item.Watched = WatchedCheck.IsChecked == true;
        item.Artwork = _artwork;
        item.Modified = DateTime.UtcNow;

        Result = item;
        DialogResult = true;
        Close();
    }

    private static int? ParseOptional(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
