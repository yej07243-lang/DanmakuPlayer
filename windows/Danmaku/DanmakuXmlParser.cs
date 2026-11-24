using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace DanmakuPlayerWin.Danmaku;

/// <summary>
/// 解析 B 站弹幕 XML（&lt;i&gt;&lt;d p="时间,模式,字号,颜色,时间戳,弹幕池,用户,行号"&gt;文本&lt;/d&gt;&lt;/i&gt;）。
/// 对缺字段、坏数字、空文本一律跳过而不是整体失败，避免一个坏节点毁掉整份弹幕；
/// 文件和 mac 版一样限制 64 MB，重复弹幕按时间/模式/文字去重。
/// </summary>
public static class DanmakuXmlParser
{
    /// <summary>mac 版同样在 64 MB 处拒绝，避免损坏文件把内存吃光。</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public static DanmakuTrack ParseFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("弹幕文件不存在", path);
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException($"弹幕文件 {info.Length / 1024.0 / 1024.0:0.0} MB，超过 64 MB 上限");

        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"XML 损坏，无法解析：{ex.Message}", ex);
        }

        var items = new List<DanmakuItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in doc.Descendants("d"))
        {
            var p = (string?)node.Attribute("p");
            if (string.IsNullOrWhiteSpace(p)) continue;

            var fields = p.Split(',');
            if (fields.Length < 4) continue;

            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) continue;
            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode)) continue;

            // 只支持 mac 版覆盖的 1–6：1/2/3 滚动、4 底部、5 顶部、6 逆向
            if (mode is < 1 or > 6) continue;
            if (time < 0) time = 0;

            var fontSize = 25;
            if (int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fs)) fontSize = fs;

            var color = 0xFFFFFF;
            if (int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) color = c;

            var text = (node.Value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0) continue;

            // B 站 XML 偶尔会重复下发同一条；同一时刻同模式同文字保留一条
            var key = $"{time:0.000}|{mode}|{text}";
            if (!seen.Add(key)) continue;

            items.Add(new DanmakuItem(time, mode, fontSize, color, text));
        }

        items.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new DanmakuTrack(Path.GetFileName(path), items);
    }
}
