using System.Security.Cryptography;
using System.Text;

namespace DanmakuPlayerWin.Bili;

/// <summary>
/// B 站网页接口的 WBI 签名：把 img_key + sub_key 按固定表打散成 mixin key，
/// 再对排序后的参数串做 MD5 得到 w_rid。算法照 macOS 版 <c>WbiSigning</c>。
/// </summary>
public static class WbiSigning
{
    private static readonly int[] Order =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
    };

    private const string Unreserved =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>把 64 位十六进制的 img_key + sub_key 打散成 32 位 mixin key。</summary>
    public static string MixKey(string imgKey, string subKey)
    {
        var combined = imgKey + subKey;
        if (combined.Length != 64 || combined.Any(c => !(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'))))
            throw new BiliException("B 站搜索签名参数无效，请稍后重试");

        var builder = new StringBuilder(Order.Length);
        foreach (var index in Order) builder.Append(combined[index]);
        return builder.ToString();
    }

    /// <summary>返回已带 wts 与 w_rid 的参数（按 key 排序），可直接拼进 query。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Sign(
        IReadOnlyDictionary<string, string> parameters, string imgKey, string subKey, long timestamp)
    {
        var mix = MixKey(imgKey, subKey);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            // B 站会丢掉这几个字符，签名前必须保持一致
            values[key] = new string(value.Where(c => !"!'()*".Contains(c)).ToArray());
        }
        values["wts"] = timestamp.ToString();

        var query = string.Join("&", values.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{Encode(k)}={Encode(values[k])}"));

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + mix));
        values["w_rid"] = Convert.ToHexString(hash).ToLowerInvariant();

        return values.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .ToList();
    }

    public static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (Unreserved.Contains(c)) builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
