namespace DanmakuPlayerWin.Danmaku;

/// <summary>一条弹幕。时间单位秒，Mode 沿用 B 站定义：1/2/3 滚动，4 底部，5 顶部，6 逆向。</summary>
public sealed record DanmakuItem(double Time, int Mode, int FontSize, int Color, string Text);

/// <summary>一份解析后的弹幕轨道。</summary>
public sealed class DanmakuTrack
{
    public DanmakuTrack(string source, IReadOnlyList<DanmakuItem> items)
    {
        Source = source;
        Items = items;
    }

    public string Source { get; }
    public IReadOnlyList<DanmakuItem> Items { get; }

    public IReadOnlyDictionary<int, int> ModeCounts =>
        Items.GroupBy(i => i.Mode).ToDictionary(g => g.Key, g => g.Count());
}
