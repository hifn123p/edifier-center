namespace EdifierCenter.Core.Model;

/// <summary>
/// 声学调校（低频切除 / 滚降斜率 / 空间声学 / 桌面反射控制）。
/// 载荷结构（docs/02 §8）：0xD5 响应 [mode][index][byte0][lowCut][slope][space][desktop]
/// （部分固件为 6 字节、无 byte0；byte0=0xFF 表示未知，回写时保留查询值）。
/// </summary>
public readonly record struct AcousticTuning(
    byte Mode,
    byte Index,
    byte Byte0,
    byte LowCutoffHz,
    byte LowCutoffSlope,
    byte AcousticSpace,
    byte DesktopControl)
{
    public const byte ResetFlag = 1; // byte0=1 → DSP 复位声学调校（"恢复默认"）

    public byte[] ToPayload() =>
        [Mode, Index, Byte0, LowCutoffHz, LowCutoffSlope, AcousticSpace, DesktopControl];

    public static readonly byte[] SlopesDbPerOct = [6, 12, 18, 24];

    /// <summary>解析 0xD5 响应（含 mode 在内的完整载荷）。</summary>
    public static bool TryParse(ReadOnlySpan<byte> p, out AcousticTuning t)
    {
        t = default;
        if (p.Length < 6) return false;
        byte index, byte0, lowCut, slope, space, desktop;
        if (p.Length == 6)
        {
            index = p[1]; byte0 = 0xFF; lowCut = p[2]; slope = p[3]; space = p[4]; desktop = p[5];
        }
        else
        {
            index = p[1]; byte0 = p[2]; lowCut = p[3]; slope = p[4]; space = p[5]; desktop = p[6];
        }
        // 斜率归一化：部分固件直接上报 6/12/18/24（docs/02 §8）
        slope = slope switch
        {
            6 => 0,
            12 => 1,
            18 => 2,
            24 => 3,
            > 3 => 0,
            _ => slope,
        };
        if (lowCut < 20) lowCut = 20; else if (lowCut > 100) lowCut = 100;
        if (space > 4) space = 4;
        t = new AcousticTuning(p[0], index, byte0, lowCut, slope, space, desktop == 0 ? (byte)0 : (byte)1);
        return true;
    }
}
