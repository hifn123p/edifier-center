namespace EdifierCenter.Core.Model;

/// <summary>音箱状态不可变快照——UI 与托盘只读订阅，更新经 StateStore.Apply 原子交换。</summary>
public sealed record SpeakerState
{
    public static readonly SpeakerState Empty = new();

    public bool Connected { get; init; }
    public ulong? Address { get; init; }
    public string DeviceName { get; init; } = "EDIFIER MR5BT";
    public string? Firmware { get; init; }
    public short? Rssi { get; init; }

    public int Volume { get; init; }
    public int VolumeMax { get; init; } = 30;
    public bool IsMuted => Volume <= 0;

    public byte? InputId { get; init; }
    public byte? SoundMode { get; init; }
    public byte? DecodeCode { get; init; }
    public CustomEqState? CustomEq { get; init; }
    public AcousticTuning? Acoustic { get; init; }
}

/// <summary>状态存储：不可变快照 + 变更事件（跨线程安全，UI 侧负责封送）。</summary>
public sealed class StateStore
{
    private SpeakerState _current = SpeakerState.Empty;

    public SpeakerState Current => Volatile.Read(ref _current);

    public event Action<SpeakerState>? StateChanged;

    public void Apply(Func<SpeakerState, SpeakerState> mutate)
    {
        var next = mutate(Current);
        Volatile.Write(ref _current, next);
        StateChanged?.Invoke(next);
    }
}

/// <summary>RSSI → 信号档位（docs/01 §5.4：≥−55 优秀 / −55~−70 良好 / −70~−80 一般 / 其余 弱）。</summary>
public static class SignalQuality
{
    public static (string Label, int Bars) FromRssi(short rssi, Mr5Profile.SignalDef? thresholds = null)
    {
        thresholds ??= new Mr5Profile.SignalDef();
        if (rssi >= thresholds.Excellent) return ("优秀", 4);
        if (rssi >= thresholds.Good) return ("良好", 3);
        if (rssi >= thresholds.Fair) return ("一般", 2);
        return ("弱", 1);
    }
}
