using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmakuPlayerWin.Bili;

/// <summary>解析出来的一个可拉弹幕的目标（BV 的某一 P，或番剧的某一集）。</summary>
public sealed record BiliTarget(
    string Bvid,
    long Cid,
    string Title,
    int Page,
    double DurationSeconds,
    string? EpId = null,
    string? SeasonId = null)
{
    public string Label
    {
        get
        {
            var name = Page > 0 ? $"P{Page} · {Title}" : Title;
            return $"{name}  ({TimeSpan.FromSeconds(DurationSeconds):m\\:ss})";
        }
    }
}

public sealed record BiliCandidate(string? Bvid, string? SeasonId, string Title, string Author, string Duration)
{
    public string Label => string.Join("  ·  ", new[] { Title, Author, Duration }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public sealed class BiliException : Exception
{
    public BiliException(string message) : base(message) { }
}

/// <summary>
/// B 站来源解析与弹幕拉取。只做匿名可访问的接口：
/// video 的 x/web-interface/view（BV/av）、pgc 的 season（ep/ss）、x/v1/dm/list.so（弹幕 XML）。
/// </summary>
public sealed class BiliClient : IDisposable
{
    private static readonly Regex BvPattern = new(@"BV[0-9A-Za-z]{10}", RegexOptions.Compiled);
    private static readonly Regex EpPattern = new(@"ep(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SsPattern = new(@"ss(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AvPattern = new(@"av(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private (string Img, string Sub, DateTime At)? _searchKeys;

    public BiliClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = true,
            CookieContainer = _cookies
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    public static string CacheDirectory
    {
        get
        {
            var dir = AppPaths.CacheDirectory;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>把用户粘贴的链接/编号解析成一个或多个候选目标（番剧整季、BV 多 P 会有多条）。</summary>
    public IReadOnlyList<BiliTarget> Resolve(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new BiliException("链接是空的");

        var text = input.Trim();
        if (text.Contains("b23.tv", StringComparison.OrdinalIgnoreCase))
        {
            text = ExpandShortLink(text);
        }

        var page = ExtractQueryInt(text, "p");

        if (BvPattern.Match(text) is { Success: true } bvMatch)
        {
            var target = FetchVideo(bvMatch.Value, page);
            return target;
        }

        if (AvPattern.Match(text) is { Success: true } avMatch)
        {
            return FetchVideo($"av{avMatch.Groups[1].Value}", page);
        }

        if (EpPattern.Match(text) is { Success: true } epMatch)
        {
            return FetchSeason(epId: epMatch.Groups[1].Value);
        }

        if (SsPattern.Match(text) is { Success: true } ssMatch)
        {
            return FetchSeason(seasonId: ssMatch.Groups[1].Value);
        }

        throw new BiliException("认不出这个链接：需要 BV 号、av 号、ep/ss 番剧链接，或 b23.tv 短链");
    }

    /// <summary>关键词搜索：official=false 搜视频，true 搜 B 站官方影视（番剧/电影）。</summary>
    public IReadOnlyList<BiliCandidate> Search(string keyword, bool official = false)
    {
        if (string.IsNullOrWhiteSpace(keyword)) throw new BiliException("搜索词是空的");

        var (imgKey, subKey) = PrepareSearchKeys();
        var signed = WbiSigning.Sign(new Dictionary<string, string>
        {
            ["search_type"] = official ? "media_ft" : "video",
            ["keyword"] = keyword,
            ["page"] = "1"
        }, imgKey, subKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var query = string.Join("&", signed.Select(kv => $"{WbiSigning.Encode(kv.Key)}={WbiSigning.Encode(kv.Value)}"));
        using var doc = GetJson($"https://api.bilibili.com/x/web-interface/wbi/search/type?{query}");
        var root = doc.RootElement;
        RequireOk(root, "搜索");

        var data = root.GetProperty("data");
        if (data.TryGetProperty("v_voucher", out _))
            throw new BiliException("B 站要求人机验证，暂时搜不了；可以改用「B 站链接…」粘贴视频链接");

        var result = new List<BiliCandidate>();
        if (data.TryGetProperty("result", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (official)
                {
                    if (!row.TryGetProperty("season_id", out var seasonElement)) continue;
                    result.Add(new BiliCandidate(
                        null,
                        seasonElement.GetInt64().ToString(),
                        StripHtml(row.TryGetProperty("title", out var title) ? title.GetString() : null) ?? "官方影视",
                        "B站官方影视",
                        row.TryGetProperty("index_show", out var index) ? index.GetString() ?? string.Empty : string.Empty));
                }
                else
                {
                    var bvid = row.TryGetProperty("bvid", out var bvidElement) ? bvidElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(bvid) || BvPattern.Match(bvid) is not { Success: true }) continue;
                    result.Add(new BiliCandidate(
                        bvid,
                        null,
                        StripHtml(row.TryGetProperty("title", out var title) ? title.GetString() : null) ?? bvid,
                        row.TryGetProperty("author", out var author) ? author.GetString() ?? string.Empty : string.Empty,
                        row.TryGetProperty("duration", out var duration) ? duration.GetString() ?? string.Empty : string.Empty));
                }
            }
        }

        return result;
    }

    /// <summary>把搜索结果解析成可拉弹幕的目标：番剧给整季分集，视频给每一 P。</summary>
    public IReadOnlyList<BiliTarget> ResolveCandidate(BiliCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.SeasonId)) return FetchSeason(seasonId: candidate.SeasonId);
        if (!string.IsNullOrWhiteSpace(candidate.Bvid)) return FetchVideo(candidate.Bvid, 0);
        throw new BiliException("这个候选项没有可用的视频");
    }

    private (string Img, string Sub) PrepareSearchKeys()
    {
        if (_searchKeys is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromHours(6))
            return (cached.Img, cached.Sub);

        EnsureGuestCookies();

        // 未登录时 nav 会返回 code -101，但 data.wbi_img 仍然给公开密钥
        using var doc = GetJson("https://api.bilibili.com/x/web-interface/nav");
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("wbi_img", out var images))
            throw new BiliException("B 站搜索暂不可用（拿不到签名密钥），可以改用「B 站链接…」");

        var imgUrl = images.TryGetProperty("img_url", out var imgElement) ? imgElement.GetString() : null;
        var subUrl = images.TryGetProperty("sub_url", out var subElement) ? subElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(imgUrl) || string.IsNullOrWhiteSpace(subUrl))
            throw new BiliException("B 站搜索暂不可用（签名密钥为空）");

        string Key(string url) => Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
        var keys = (Key(imgUrl), Key(subUrl), DateTime.UtcNow);
        _searchKeys = keys;
        return (keys.Item1, keys.Item2);
    }

    private void EnsureGuestCookies()
    {
        if (_cookies.GetCookies(new Uri("https://www.bilibili.com/"))["buvid3"] != null) return;

        try
        {
            using var doc = GetJson("https://api.bilibili.com/x/frontend/finger/spi");
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;

            foreach (var (field, name) in new[] { ("b_3", "buvid3"), ("b_4", "buvid4") })
            {
                if (!data.TryGetProperty(field, out var value)) continue;
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                _cookies.Add(new Uri("https://www.bilibili.com/"), new Cookie(name, text, "/", ".bilibili.com"));
            }
        }
        catch
        {
            // 拿不到游客 cookie 也能试搜索，失败会给出明确错误
        }
    }

    private static string? StripHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var plain = Regex.Replace(text, "<[^>]+>", string.Empty)
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">");
        return plain.Trim();
    }

    private string ExpandShortLink(string text)
    {
        var url = text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : "https://" + text;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new BiliException("短链格式不对");

        using var response = _http.GetAsync(uri).GetAwaiter().GetResult();
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (finalUri.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)) return finalUri.AbsoluteUri;

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var match = Regex.Match(body, @"https?://[^\""'<> ]+bilibili\.com[^\""'<> ]*");
        return match.Success ? match.Value : finalUri.AbsoluteUri;
    }

    private static int ExtractQueryInt(string text, string key)
    {
        var match = Regex.Match(text, $@"[?&]{key}=(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }

    private List<BiliTarget> FetchVideo(string id, int page)
    {
        var url = $"https://api.bilibili.com/x/web-interface/view?{(id.StartsWith("av", StringComparison.OrdinalIgnoreCase) ? "aid" : "bvid")}={Uri.EscapeDataString(id.Replace("av", string.Empty, StringComparison.OrdinalIgnoreCase))}";
        using var doc = GetJson(url);
        var root = doc.RootElement;
        RequireOk(root, "视频信息");

        var data = root.GetProperty("data");
        var title = data.GetProperty("title").GetString() ?? id;
        var result = new List<BiliTarget>();

        if (data.TryGetProperty("pages", out var pages) && pages.GetArrayLength() > 0)
        {
            foreach (var pageElement in pages.EnumerateArray())
            {
                var pageNumber = pageElement.GetProperty("page").GetInt32();
                if (page > 0 && pageNumber != page) continue;

                result.Add(new BiliTarget(
                    data.GetProperty("bvid").GetString() ?? id,
                    pageElement.GetProperty("cid").GetInt64(),
                    pageElement.TryGetProperty("part", out var part) && !string.IsNullOrWhiteSpace(part.GetString())
                        ? $"{title} · {part.GetString()}"
                        : title,
                    pageNumber,
                    pageElement.TryGetProperty("duration", out var duration) ? duration.GetDouble() : 0));
            }
        }

        if (result.Count == 0)
        {
            if (page > 0) throw new BiliException($"这个视频没有第 {page} P（P 号从 1 开始）");

            result.Add(new BiliTarget(
                data.GetProperty("bvid").GetString() ?? id,
                data.GetProperty("cid").GetInt64(),
                title,
                1,
                data.TryGetProperty("duration", out var duration) ? duration.GetDouble() : 0));
        }

        return result;
    }

    private List<BiliTarget> FetchSeason(string? seasonId = null, string? epId = null)
    {
        var query = seasonId != null ? $"season_id={seasonId}" : $"ep_id={epId}";
        using var doc = GetJson($"https://api.bilibili.com/pgc/view/web/season?{query}");
        var root = doc.RootElement;
        RequireOk(root, "番剧信息");

        var result = root.GetProperty("result");
        var season = result.TryGetProperty("season_id", out var seasonElement) ? seasonElement.GetInt64().ToString() : seasonId;
        var seasonTitle = result.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var targets = new List<BiliTarget>();

        if (result.TryGetProperty("episodes", out var episodes))
        {
            foreach (var episode in episodes.EnumerateArray())
            {
                var currentEp = episode.TryGetProperty("ep_id", out var epElement) ? epElement.GetInt64().ToString() : null;
                // 用 ep_id 打开时只保留那一集
                if (epId != null && currentEp != epId) continue;

                var longTitle = episode.TryGetProperty("long_title", out var longElement) ? longElement.GetString() : null;
                var shortTitle = episode.TryGetProperty("title", out var shortElement) ? shortElement.GetString() : null;
                var label = !string.IsNullOrWhiteSpace(longTitle) ? longTitle : shortTitle ?? "未命名";

                targets.Add(new BiliTarget(
                    episode.TryGetProperty("bvid", out var bv) ? bv.GetString() ?? string.Empty : string.Empty,
                    episode.GetProperty("cid").GetInt64(),
                    string.IsNullOrWhiteSpace(seasonTitle) ? label : $"{seasonTitle} · {label}",
                    episode.TryGetProperty("title", out var number) && int.TryParse(number.GetString(), out var value) ? value : 0,
                    episode.TryGetProperty("duration", out var duration) ? duration.GetInt64() / 1000.0 : 0,
                    currentEp,
                    season));
            }
        }

        if (targets.Count == 0) throw new BiliException("这个番剧里没有解析到可用的分集");
        return targets;
    }

    /// <summary>拉取弹幕 XML 并缓存到本地，返回文件路径。</summary>
    public string FetchDanmaku(long cid, bool refresh = false)
    {
        var path = Path.Combine(CacheDirectory, $"{cid}.xml");
        if (!refresh && File.Exists(path) && new FileInfo(path).Length > 0) return path;

        using var response = _http.GetAsync($"https://api.bilibili.com/x/v1/dm/list.so?oid={cid}").GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new BiliException($"弹幕接口返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）");

        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.Length == 0) throw new BiliException("弹幕接口返回空内容");

        var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (!text.StartsWith("<?xml", StringComparison.Ordinal)) throw new BiliException("弹幕接口返回的不是 XML（可能被风控或需要登录）");

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private JsonDocument GetJson(string url)
    {
        try
        {
            using var response = _http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new BiliException($"接口返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）");

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonDocument.Parse(body);
        }
        catch (BiliException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BiliException($"请求失败：{ex.Message}");
        }
    }

    private static void RequireOk(JsonElement root, string what)
    {
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : 0;
        var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;

        if (code == 0) return;
        var hint = code switch
        {
            -404 => "内容不存在或已下架",
            -403 => "没有访问权限（可能需要登录）",
            62002 => "该内容不可见",
            62004 => "该内容审核中",
            _ => message ?? "未知错误"
        };
        throw new BiliException($"{what}解析失败：{hint}（code {code}）");
    }

    public void Dispose() => _http.Dispose();
}
