using System.Text.Json;

namespace EdifierCenter.Core.Model;

public sealed class SoundModeDef
{
    public byte? Id { get; init; }
    public string Label { get; init; } = "";
}

public sealed class RangeDef
{
    public int Min { get; init; }
    public int Max { get; init; }
    public int Step { get; init; }
}

/// <summary>
/// MR5BT 型号参数（profiles/mr5.json）。“每型号不同”的东西全部数据化，
/// 代码只认协议骨架；Phase 0 标定结果回填本文件即可，无需改代码。
/// </summary>
public sealed class Mr5Profile
{
    public string Model { get; init; } = "EDIFIER MR5BT";
    public DiscoveryDef Discovery { get; init; } = new();
    public byte AppCode { get; init; } = FrameCodec.DefaultAppCode;
    public VolumeDef Volume { get; init; } = new();
    public List<SoundModeDef> SoundModes { get; init; } = [];
    public CustomEqDef CustomEq { get; init; } = new();
    public AcousticDef AcousticTuning { get; init; } = new();
    public InputsDef Inputs { get; init; } = new();
    public Dictionary<string, string> DecodeStatus { get; init; } = [];
    public SignalDef SignalBarsDbm { get; init; } = new();
    public SessionDef Session { get; init; } = new();

    public int EffectiveZeroGainCode => CustomEq.GainCodeZeroDb ?? 6; // (code−6)×0.5dB 假设，待标定
    public double EffectiveDbPerCode => CustomEq.GainDbPerCode ?? 0.5;

    public string? LabelForSoundMode(byte id) =>
        SoundModes.FirstOrDefault(m => m.Id == id)?.Label;

    public string? LabelForInput(byte id) =>
        Inputs.Labels.TryGetValue(id.ToString(), out var v) ? v : null;

    public string? LabelForDecode(byte code) =>
        DecodeStatus.TryGetValue(code.ToString(), out var v) ? v : null;

    public static Mr5Profile LoadEmbedded(Func<string?, Stream?> openResource)
    {
        using var stream = openResource("mr5.json")
            ?? throw new InvalidOperationException("未找到内嵌 profiles/mr5.json");
        return Load(stream);
    }

    public static Mr5Profile Load(Stream json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<Mr5Profile>(json, options)
            ?? throw new InvalidOperationException("profiles/mr5.json 反序列化失败");
    }

    public sealed class DiscoveryDef
    {
        public string[] SearchServiceUuids { get; init; } = [];
        public string[] ServiceUuids { get; init; } = [];
        public string NotifyCharacteristicUuid { get; init; } = "";
        public string WriteCharacteristicUuid { get; init; } = "";
        public ushort ManufacturerId { get; init; } = 0x07E0;
        public string AdvertisedName { get; init; } = "EDIFIER BLE";
    }

    public sealed class VolumeDef
    {
        public int Max { get; init; } = 30;
        public int ThrottleMs { get; init; } = 150;
    }

    public sealed class CustomEqDef
    {
        public byte EqIndex { get; init; } = CustomEqState.Mr5EqIndex;
        public int BandCount { get; init; } = CustomEqState.BandCount;
        public ushort[] FrequenciesHz { get; init; } = [62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        public double StepDb { get; init; } = 0.5;
        public int[] UiRangeDb { get; init; } = [-6, 6];
        public int? GainCodeZeroDb { get; init; }
        public double? GainDbPerCode { get; init; }
    }

    public sealed class AcousticDef
    {
        public RangeDef LowCutoffHz { get; init; } = new() { Min = 20, Max = 100, Step = 5 };
    }

    public sealed class InputsDef
    {
        public byte? Group { get; init; }
        public Dictionary<string, string> Labels { get; init; } = [];
    }

    public sealed class SignalDef
    {
        public int Excellent { get; init; } = -55;
        public int Good { get; init; } = -70;
        public int Fair { get; init; } = -80;
    }

    public sealed class SessionDef
    {
        public string[] InitialRead { get; init; } = ["support", "volume", "eq", "customEq", "version", "state"];
        public int PollIntervalSeconds { get; init; } = 5;
        public int[] ReconnectBackoffSeconds { get; init; } = [1, 2, 5, 10, 30];
    }
}
