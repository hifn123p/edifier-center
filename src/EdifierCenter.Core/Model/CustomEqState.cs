namespace EdifierCenter.Core.Model;

/// <summary>自定义 EQ 单波段。</summary>
public sealed class CustomEqBand
{
    public byte Index { get; init; }
    public ushort FrequencyHz { get; init; }
    public byte GainCode { get; init; }
}

/// <summary>
/// 自定义 EQ 状态（MR5BT 为 eqIndex=12 布局：9 段 × 4 字节）。
/// 载荷结构（docs/02 §9）：[12][09][byte0][9×(index freqHi freqLo gain)][4B 时间戳][UTF-8 名称]
/// </summary>
public sealed class CustomEqState
{
    public const byte Mr5EqIndex = 12;
    public const int BandCount = 9;

    public byte EqIndex { get; init; }
    /// <summary>查询响应第 3 字节，写单段时原样回写。</summary>
    public byte Byte0 { get; init; }
    public IReadOnlyList<CustomEqBand> Bands { get; init; } = [];
    public byte[]? DateBytes { get; init; }
    public string? Name { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> p, out CustomEqState? state)
    {
        state = null;
        // [eqIndex][bandCount] + byte0 + 9×4
        if (p.Length < 2 + 1 + BandCount * 4) return false;
        if (p[0] != Mr5EqIndex) return false; // 仅支持 MR5BT 的 9 段布局
        var count = Math.Min((int)p[1], BandCount);
        var byte0 = p[2];
        var bands = new List<CustomEqBand>(count);
        for (var k = 0; k < count; k++)
        {
            var @base = 3 + k * 4;
            bands.Add(new CustomEqBand
            {
                Index = p[@base],
                FrequencyHz = (ushort)((p[@base + 1] << 8) | p[@base + 2]),
                GainCode = p[@base + 3],
            });
        }
        byte[]? date = null;
        string? name = null;
        var tailStart = 3 + BandCount * 4;
        if (p.Length >= tailStart + 4)
        {
            date = p.Slice(tailStart, 4).ToArray();
            if (p.Length > tailStart + 4)
            {
                name = System.Text.Encoding.UTF8.GetString(p[(tailStart + 4)..]).Trim('\0', ' ');
                if (name.Length == 0) name = null;
            }
        }
        state = new CustomEqState { EqIndex = p[0], Byte0 = byte0, Bands = bands, DateBytes = date, Name = name };
        return true;
    }
}
