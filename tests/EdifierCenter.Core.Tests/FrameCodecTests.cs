using EdifierCenter.Core.Model;
using EdifierCenter.Core.Protocol;
using Xunit;

namespace EdifierCenter.Core.Tests;

/// <summary>构造"音箱响应帧"：主机帧改头 BB 并修正校验和，便于测试解析器。</summary>
internal static class TestFrames
{
    public static byte[] SpeakerReply(CommandId command, params byte[] payload)
    {
        var host = FrameCodec.Encode((byte)command, payload);
        var reply = (byte[])host.Clone();
        reply[0] = FrameCodec.DataHeader;
        byte sum = 0;
        for (var i = 0; i < reply.Length - 1; i++) sum += reply[i];
        reply[^1] = sum;
        return reply;
    }
}

public class FrameCodecTests
{
    [Fact]
    public void Encode_GetVolume_MatchesKnownVector()
    {
        // docs/02 §6：AA 0C 66 00 00 1C
        var frame = CommandFactory.Query(CommandId.GetVolume);
        Assert.Equal(new byte[] { 0xAA, 0x0C, 0x66, 0x00, 0x00, 0x1C }, frame);
    }

    [Fact]
    public void Encode_SetVolume30_MatchesKnownVector()
    {
        // docs/02 §6：AA 0C 67 00 01 1E 3C
        var frame = CommandFactory.SetVolume(30, max: 30);
        Assert.Equal(new byte[] { 0xAA, 0x0C, 0x67, 0x00, 0x01, 0x1E, 0x3C }, frame);
    }

    [Fact]
    public void Encode_SetVolume_ClampedToMax()
    {
        var frame = CommandFactory.SetVolume(99, max: 30);
        Assert.Equal(30, frame[5]);
    }

    [Fact]
    public void Encode_ChecksumIsSumOfPrefix()
    {
        var frame = FrameCodec.Encode(0x44, [0x06, 0x00, 0x00, 0x3E, 0x07]);
        byte sum = 0;
        for (var i = 0; i < frame.Length - 1; i++) sum += frame[i];
        Assert.Equal(sum, frame[^1]);
    }

    [Fact]
    public void TryParse_ValidResponse_Parses()
    {
        // docs/02 §6：BB 0C 66 00 01 2A 58（样例 current=0x2A）
        Assert.True(FrameCodec.TryParse(new byte[] { 0xBB, 0x0C, 0x66, 0x00, 0x01, 0x2A, 0x58 }, out var frame));
        Assert.Equal(FrameCodec.DataHeader, frame.Type);
        Assert.Equal((byte)CommandId.GetVolume, frame.Command);
        Assert.Equal(new byte[] { 0x2A }, frame.Payload);
        Assert.True(frame.ChecksumValid);
    }

    [Fact]
    public void TryParse_BadLength_ReturnsFalse()
    {
        // LEN 声称 2，实际总长 6（应为 8）
        Assert.False(FrameCodec.TryParse(new byte[] { 0xBB, 0x0C, 0x66, 0x00, 0x02, 0x1C }, out _));
    }

    [Fact]
    public void TryParse_BadChecksum_ParsesWithInvalidFlag()
    {
        var reply = TestFrames.SpeakerReply(CommandId.GetVolume, 0x1E, 0x0A);
        reply[^1] ^= 0xFF;
        Assert.True(FrameCodec.TryParse(reply, out var frame));
        Assert.False(frame.ChecksumValid);
    }
}

public class ResponseParserTests
{
    [Fact]
    public void Parse_VolumeWithMax()
    {
        Assert.True(ResponseParser.TryParse(
            FrameCodec.ParseOrThrow(TestFrames.SpeakerReply(CommandId.GetVolume, 0x1E, 0x0A)), out var u));
        Assert.Equal(UpdateKind.Volume, u.Kind);
        Assert.Equal(0x1E, u.VolumeMax);
        Assert.Equal(0x0A, u.Volume);
    }

    [Fact]
    public void Parse_VolumePushSingleByte_KeepsMax()
    {
        Assert.True(ResponseParser.TryParse(
            FrameCodec.ParseOrThrow(TestFrames.SpeakerReply(CommandId.GetVolume, 0x16)), out var u));
        Assert.Equal(22, u.Volume);
        Assert.Equal(-1, u.VolumeMax);
    }

    [Fact]
    public void Parse_BadChecksum_ReturnsFalse()
    {
        var reply = TestFrames.SpeakerReply(CommandId.GetVolume, 0x1E, 0x0A);
        reply[^1] ^= 0xFF;
        var frame = FrameCodec.ParseOrThrow(reply);
        Assert.False(ResponseParser.TryParse(frame, out _));
    }

    [Fact]
    public void Parse_Ack()
    {
        var ack = TestFrames.SpeakerReply(CommandId.SetVolume, 0x01);
        ack[0] = FrameCodec.AckHeader;
        byte sum = 0;
        for (var i = 0; i < ack.Length - 1; i++) sum += ack[i];
        ack[^1] = sum;
        Assert.True(ResponseParser.TryParse(FrameCodec.ParseOrThrow(ack), out var u));
        Assert.Equal(UpdateKind.Ack, u.Kind);
    }

    [Fact]
    public void Parse_DecodeStatus()
    {
        // 05 = 有线 Hi-Res（docs/02 §5）
        Assert.True(ResponseParser.TryParse(
            FrameCodec.ParseOrThrow(TestFrames.SpeakerReply(CommandId.GetDecodeStatus, 0x05)), out var u));
        Assert.Equal(UpdateKind.DecodeStatus, u.Kind);
        Assert.Equal((byte)5, u.DecodeCode);
    }

    [Fact]
    public void Parse_Version()
    {
        // 实机固件 1.0.3
        Assert.True(ResponseParser.TryParse(
            FrameCodec.ParseOrThrow(TestFrames.SpeakerReply(CommandId.GetVersion, 0x01, 0x00, 0x03)), out var u));
        Assert.Equal("1.0.3", u.Version);
    }

    [Fact]
    public void Parse_Input_LastByteWins()
    {
        Assert.True(ResponseParser.TryParse(
            FrameCodec.ParseOrThrow(TestFrames.SpeakerReply(CommandId.GetInput, 0x03, 0x01)), out var u));
        Assert.Equal((byte)1, u.InputId);
    }
}
