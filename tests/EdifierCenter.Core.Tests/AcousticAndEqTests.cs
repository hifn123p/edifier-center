using EdifierCenter.Core.Model;
using EdifierCenter.Core.Protocol;

namespace EdifierCenter.Core.Tests;

public class AcousticTuningTests
{
    [Fact]
    public void Parse_KnownVector()
    {
        // docs/02 §6：30Hz / -24dB/oct / -2dB / 桌面开（omarchy 实机验证）
        Assert.True(AcousticTuning.TryParse(new byte[] { 0x02, 0x02, 0x00, 0x1E, 0x03, 0x02, 0x01 }, out var t));
        Assert.Equal((byte)2, t.Mode);
        Assert.Equal((byte)2, t.Index);
        Assert.Equal((byte)0, t.Byte0);
        Assert.Equal(30, t.LowCutoffHz);
        Assert.Equal((byte)3, t.LowCutoffSlope);
        Assert.Equal((byte)2, t.AcousticSpace);
        Assert.Equal((byte)1, t.DesktopControl);
    }

    [Fact]
    public void Parse_NormalizesSlopeBytes()
    {
        // 部分固件直接上报 6/12/18/24
        Assert.True(AcousticTuning.TryParse(new byte[] { 0x01, 0x01, 0x00, 80, 24, 0, 0 }, out var t));
        Assert.Equal((byte)3, t.LowCutoffSlope);
        Assert.True(AcousticTuning.TryParse(new byte[] { 0x01, 0x01, 0x00, 80, 6, 0, 0 }, out t));
        Assert.Equal((byte)0, t.LowCutoffSlope);
        Assert.True(AcousticTuning.TryParse(new byte[] { 0x01, 0x01, 0x00, 80, 99, 0, 0 }, out t));
        Assert.Equal((byte)0, t.LowCutoffSlope);
    }

    [Fact]
    public void Parse_SixByteVariant_NoByte0()
    {
        // EC：len==6 → [mode][index][lowCut][slope][space][desktop]，byte0=0xFF
        Assert.True(AcousticTuning.TryParse(new byte[] { 0x00, 0x01, 0x50, 0x00, 0x00, 0x01 }, out var t));
        Assert.Equal((byte)0xFF, t.Byte0);
        Assert.Equal(80, t.LowCutoffHz);
        Assert.Equal((byte)1, t.DesktopControl);
    }

    [Fact]
    public void Parse_ClampsValues()
    {
        Assert.True(AcousticTuning.TryParse(new byte[] { 0x00, 0x01, 0x00, 10, 0, 9, 5 }, out var t));
        Assert.Equal(20, t.LowCutoffHz);
        Assert.Equal((byte)4, t.AcousticSpace);
        Assert.Equal((byte)1, t.DesktopControl);
    }

    [Fact]
    public void RoundTrip_PayloadMatches()
    {
        var t = new AcousticTuning(2, 2, 0, 40, 1, 4, 1);
        var frame = CommandFactory.SetAcoustic(t);
        // AA 0C C4 00 07 <7B payload> <CK>
        Assert.Equal((byte)CommandId.SetEq, frame[2]);
        Assert.Equal(7, (frame[3] << 8) | frame[4]);
        Assert.Equal(t.ToPayload(), frame.Skip(5).Take(7).ToArray());
    }
}

public class CustomEqTests
{
    private static byte[] BuildQueryPayload(byte[] gains, byte byte0 = 6, string? name = null)
    {
        // [12][09][byte0][9×(index freqHi freqLo gain)][4B date][name]
        var payload = new List<byte> { CustomEqState.Mr5EqIndex, CustomEqState.BandCount, byte0 };
        ushort[] freqs = [62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        for (var i = 0; i < 9; i++)
        {
            payload.Add((byte)i);
            payload.Add((byte)(freqs[i] >> 8));
            payload.Add((byte)(freqs[i] & 0xFF));
            payload.Add(gains[i]);
        }
        payload.AddRange([0x00, 0x00, 0x00, 0x00]); // date
        if (name != null) payload.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
        return [.. payload];
    }

    [Fact]
    public void Parse_NineBands()
    {
        byte[] gains = [7, 8, 9, 6, 5, 9, 8, 7, 6];
        Assert.True(CustomEqState.TryParse(BuildQueryPayload(gains), out var eq));
        Assert.NotNull(eq);
        Assert.Equal(9, eq.Bands.Count);
        Assert.Equal((byte)6, eq.Byte0);
        Assert.Equal(62, eq.Bands[0].FrequencyHz);
        Assert.Equal((byte)7, eq.Bands[0].GainCode);
        Assert.Equal(16000, eq.Bands[8].FrequencyHz);
    }

    [Fact]
    public void Parse_WithDateAndName()
    {
        Assert.True(CustomEqState.TryParse(BuildQueryPayload(new byte[9], name: "我的音效"), out var eq));
        Assert.NotNull(eq);
        Assert.Equal("我的音效", eq.Name);
        Assert.NotNull(eq.DateBytes);
    }

    [Fact]
    public void Parse_RejectsOtherEqIndex()
    {
        var payload = BuildQueryPayload(new byte[9]);
        payload[0] = 7;
        Assert.False(CustomEqState.TryParse(payload, out _));
    }

    [Fact]
    public void Parse_TooShort_ReturnsFalse()
    {
        Assert.False(CustomEqState.TryParse(new byte[] { 12, 9, 6, 0, 0 }, out _));
    }

    [Fact]
    public void BuildBandWrite_MatchesDocumentedFormat()
    {
        byte[] gains = [7, 8, 9, 6, 5, 9, 8, 7, 6];
        Assert.True(CustomEqState.TryParse(BuildQueryPayload(gains), out var eq));
        var frame = CommandFactory.SetCustomEqBand(eq!, 2);
        // [AA][0C][44][00][05][byte0][index=2][freqHi=0x00][freqLo=0xFA][gain=9][CK]
        Assert.Equal((byte)CommandId.SetCustomEqBand, frame[2]);
        Assert.Equal(5, (frame[3] << 8) | frame[4]);
        Assert.Equal(new byte[] { 6, 2, 0x00, 0xFA, 9 }, frame.Skip(5).Take(5).ToArray());
    }
}
