using EdifierCenter.Core.Model;

namespace EdifierCenter.Core.Protocol;

/// <summary>命令构造（全部基于白名单 CommandId）。音量值由调用方夹取到 profile.VolumeMax 内。</summary>
public static class CommandFactory
{
    public static byte[] Query(CommandId id) => FrameCodec.Encode((byte)id);

    public static byte[] SetVolume(int value, int max)
    {
        var v = Math.Clamp(value, 0, max);
        return FrameCodec.Encode((byte)CommandId.SetVolume, [(byte)v]);
    }

    public static byte[] SetSoundMode(byte mode) => FrameCodec.Encode((byte)CommandId.SetEq, [mode]);

    public static byte[] SetAcoustic(in AcousticTuning tuning) =>
        FrameCodec.Encode((byte)CommandId.SetEq, tuning.ToPayload());

    public static byte[] SetCustomEqBand(CustomEqState state, int bandIndex)
    {
        if (bandIndex < 0 || bandIndex >= state.Bands.Count)
            throw new ArgumentOutOfRangeException(nameof(bandIndex));
        var band = state.Bands[bandIndex];
        var payload = new byte[]
        {
            state.Byte0,
            band.Index,
            (byte)(band.FrequencyHz >> 8),
            (byte)(band.FrequencyHz & 0xFF),
            band.GainCode,
        };
        return FrameCodec.Encode((byte)CommandId.SetCustomEqBand, payload);
    }
}
