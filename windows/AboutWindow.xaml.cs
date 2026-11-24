using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DanmakuPlayerWin;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = $"版本 {version}";

        var logoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(logoPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(logoPath, UriKind.Absolute);
                image.EndInit();
                Logo.Source = image;
            }
            catch
            {
                // 图标读不出来不影响关于窗口
            }
        }

        ComponentsText.Text =
            "第三方组件（详见 THIRD-PARTY-NOTICES.md）：\n" +
            "· libmpv v0.42.0-225-gc2a42f414（GPL 构建，含 FFmpeg N-129222-g329157f58、libass、libplacebo）\n" +
            "· .NET 8 / WPF（MIT）\n" +
            "· Microsoft.Data.Sqlite / SQLite（MIT / Public Domain）\n\n" +
            $"数据目录：{AppPaths.DataDirectory}";
    }

    private void OnOpenLicenseClick(object sender, RoutedEventArgs e)
    {
        var license = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        if (!File.Exists(license))
        {
            MessageBox.Show(this, "找不到 LICENSE 文件（它随发布包提供）。", "DanmakuPlayer");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(license) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开许可证");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
