namespace EdifierCenter.Core.Protocol;

/// <summary>ConneX v2 帧（docs/02 §3）。Type: 0xBB=数据 0xCC=ACK 0xDD=事件。</summary>
public readonly record struct Frame(byte Type, byte AppCode, byte Command, byte[] Payload, bool ChecksumValid)
{
    public string PayloadHex => Convert.ToHexString(Payload);
}

/// <summary>
/// ConneX v2 帧编解码：AA/BB/CC/DD + appCode + CMD + LEN(16BE) + payload + checksum(前缀字节和低 8 位)。
/// </summary>
public static class FrameCodec
{
    public const byte HostHeader = 0xAA;
    public const byte DataHeader = 0xBB;
    public const byte AckHeader = 0xCC;
    public const byte EventHeader = 0xDD;

    /// <summary>EdifierControl 使用 0x0C；音箱对 appCode 不做严格校验（0x06/0x0C/0xEC 均实测可用）。</summary>
    public const byte DefaultAppCode = 0x0C;

    public static byte[] Encode(byte command, ReadOnlySpan<byte> payload = default, byte appCode = DefaultAppCode)
    {
        if (payload.Length > 0xFFFF) throw new ArgumentException("载荷超过 0xFFFF 字节", nameof(payload));
        var frame = new byte[payload.Length + 6];
        frame[0] = HostHeader;
        frame[1] = appCode;
        frame[2] = command;
        frame[3] = (byte)(payload.Length >> 8);
        frame[4] = (byte)(payload.Length & 0xFF);
        payload.CopyTo(frame.AsSpan(5));
        frame[^1] = Checksum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    /// <summary>解析音箱响应帧。长度不符返回 false；校验和错误仍返回 true 但 ChecksumValid=false（由调用方丢弃）。</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out Frame frame)
    {
        frame = default;
        if (bytes.Length < 6) return false;
        var type = bytes[0];
        if (type is not (DataHeader or AckHeader or EventHeader)) return false;
        var length = (bytes[3] << 8) | bytes[4];
        if (bytes.Length != length + 6) return false;
        var expected = Checksum(bytes[..^1]);
        var payload = bytes.Slice(5, length).ToArray();
        frame = new Frame(type, bytes[1], bytes[2], payload, expected == bytes[^1]);
        return true;
    }

    /// <summary>测试与诊断辅助：解析失败直接抛出。</summary>
    public static Frame ParseOrThrow(ReadOnlySpan<byte> bytes)
        => TryParse(bytes, out var frame)
            ? frame
            : throw new FormatException($"非 ConneX v2 帧: {Convert.ToHexString(bytes.ToArray())}");

    private static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (var b in bytes) sum += b;
        return sum;
    }
}
