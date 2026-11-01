using EdifierCenter.Core.Model;

namespace EdifierCenter.Core.Protocol;

public enum UpdateKind
{
    Ack,
    Volume,
    Input,
    SoundMode,
    Acoustic,
    CustomEq,
    Version,
    DecodeStatus,
    DeviceName,
    Support,
    DeviceState,
    Other,
}

/// <summary>解析后的状态更新。值为 -1 / null 表示“本次未携带，保持原值”。</summary>
public readonly struct ParsedUpdate
{
    public UpdateKind Kind { get; init; }
    public int Volume { get; init; }
    public int VolumeMax { get; init; }
    public byte? InputId { get; init; }
    public byte? SoundMode { get; init; }
    public byte? DecodeCode { get; init; }
    public AcousticTuning? Acoustic { get; init; }
    public CustomEqState? CustomEq { get; init; }
    public string Version { get; init; }
    public string Name { get; init; }
    public string PayloadHex { get; init; }
}

/// <summary>响应/推送 → 领域更新。校验和无效的帧被丢弃（false）。</summary>
public static class ResponseParser
{
    public static bool TryParse(in Frame frame, out ParsedUpdate update)
    {
        update = default;
        if (!frame.ChecksumValid) return false;
        update = update with { PayloadHex = frame.PayloadHex };

        if (frame.Type == FrameCodec.AckHeader)
        {
            update = update with { Kind = UpdateKind.Ack };
            return true;
        }
        if (frame.Type != FrameCodec.DataHeader && frame.Type != FrameCodec.EventHeader)
            return false;

        var p = frame.Payload;
        switch (frame.Command)
        {
            case (byte)CommandId.GetVolume when p.Length >= 1:
                update = p.Length >= 2
                    ? update with { Kind = UpdateKind.Volume, VolumeMax = p[0], Volume = p[1] }
                    : update with { Kind = UpdateKind.Volume, VolumeMax = -1, Volume = p[0] };
                return true;

            case (byte)CommandId.GetInput when p.Length >= 1:
                update = update with { Kind = UpdateKind.Input, InputId = p[^1] };
                return true;

            case (byte)CommandId.GetDecodeStatus when p.Length >= 1:
                update = update with { Kind = UpdateKind.DecodeStatus, DecodeCode = p[0] };
                return true;

            case (byte)CommandId.GetVersion when p.Length >= 1:
                update = update with
                {
                    Kind = UpdateKind.Version,
                    Version = string.Join(".", p.Select(b => b.ToString())),
                };
                return true;

            case (byte)CommandId.GetEq when p.Length >= 1:
                update = update with { Kind = UpdateKind.SoundMode, SoundMode = p[0] };
                if (p.Length >= 6 && AcousticTuning.TryParse(p, out var acoustic))
                    update = update with { Kind = UpdateKind.Acoustic, SoundMode = acoustic.Mode, Acoustic = acoustic };
                return true;

            case (byte)CommandId.GetCustomEq when p.Length >= 2:
                if (!CustomEqState.TryParse(p, out var eq)) return false;
                update = update with { Kind = UpdateKind.CustomEq, CustomEq = eq };
                return true;

            case (byte)CommandId.GetDeviceName when p.Length >= 1:
                update = update with
                {
                    Kind = UpdateKind.DeviceName,
                    Name = System.Text.Encoding.UTF8.GetString(p).Trim('\0', ' '),
                };
                return true;

            case (byte)CommandId.GetSupport when p.Length >= 1:
                update = update with { Kind = UpdateKind.Support };
                return true;

            case (byte)CommandId.GetDeviceState when p.Length >= 1:
                update = update with { Kind = UpdateKind.DeviceState };
                return true;

            default:
                update = update with { Kind = UpdateKind.Other };
                return true;
        }
    }
}
