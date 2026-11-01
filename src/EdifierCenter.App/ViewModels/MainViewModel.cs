using System.IO;
using System.Windows;
using System.Windows.Threading;
using EdifierCenter.App.Services;
using EdifierCenter.Ble;
using EdifierCenter.Core;
using EdifierCenter.Core.Model;
using EdifierCenter.Core.Protocol;

namespace EdifierCenter.App.ViewModels;

/// <summary>声音模式选项。</summary>
public sealed class SoundModeVm
{
    public SoundModeVm(byte? id, string label)
    {
        Id = id;
        Label = label;
    }

    public byte? Id { get; }
    public string Label { get; }
}

/// <summary>自定义 EQ 单波段视图。</summary>
public sealed class BandVm : BindBase
{
    private double _gainDb;
    private bool _syncing;
    private readonly Action<BandVm> _onUserChange;

    public BandVm(int index, int freqHz, double gainDb, double min, double max, Action<BandVm> onUserChange)
    {
        Index = index;
        FreqLabel = freqHz >= 1000 ? $"{freqHz / 1000.0:0.#}k" : $"{freqHz}";
        Minimum = min;
        Maximum = max;
        _gainDb = gainDb;
        _onUserChange = onUserChange;
    }

    public int Index { get; }
    public string FreqLabel { get; }
    public double Minimum { get; }
    public double Maximum { get; }

    public double GainDb
    {
        get => _gainDb;
        set
        {
            if (_syncing)
            {
                Set(ref _gainDb, value);
                return;
            }
            if (Set(ref _gainDb, value)) _onUserChange(this);
        }
    }

    public string GainText => $"{GainDb:0.#}dB";

    public void SetGainSilently(double db)
    {
        _syncing = true;
        try { GainDb = db; }
        finally { _syncing = false; }
    }
}

/// <summary>
/// 主视图模型：StateStore 快照 → 可绑定属性（经 Dispatcher 封送）；UI 命令 → 会话命令队列。
/// M1 采用粗粒度刷新（Raise(string.Empty) 全量重读绑定），M2 改为精确属性通知。
/// </summary>
public sealed class MainViewModel : BindBase
{
    private readonly StateStore _store;
    private readonly EdifierSession _session;
    private readonly Mr5Profile _profile;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _volumeTimer;
    private readonly DispatcherTimer _acousticTimer;
    private readonly DispatcherTimer _bandTimer;
    private int _pendingVolume = -1;
    private int _lastNonZeroVolume = 15;
    private CustomEqState? _eqMirror;
    private AcousticTuning? _acousticMirror;
    private bool _suppressSync;

    public bool IsConnected { get; private set; }
    public string DeviceName { get; private set; } = "EDIFIER MR5BT";
    public string StatusText { get; private set; } = "未连接";
    public string SignalLine { get; private set; } = "信号 —";
    public string InputLabel { get; private set; } = "—";
    public string DecodeLabel { get; private set; } = "";
    public bool HasDecode { get; private set; }
    public int Volume { get; private set; } = 15;
    public int VolumeMax { get; private set; } = 30;
    public bool IsMuted { get; private set; }
    public bool HasEq { get; private set; }
    public bool HasAcoustic { get; private set; }
    public double LowCutoffHz { get; private set; } = 40;
    public int SlopeIndex { get; private set; } = 1;
    public int SpaceIndex { get; private set; } = 0;
    public bool DesktopOn { get; private set; }
    public bool AutoStartEnabled { get; private set; }

    public Visibility BannerVisibility => IsConnected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DecodeVisibility => HasDecode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EqVisibility => HasEq ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AcousticVisibility => HasAcoustic ? Visibility.Visible : Visibility.Collapsed;
    public string VolumeText => $"{Volume} / {VolumeMax}";
    public string MuteButtonText => IsMuted ? "取消静音" : "静音";
    public string EqMeta => "9 段";

    public List<SoundModeVm> SoundModes { get; }
    public SoundModeVm? SelectedSoundMode
    {
        get => _selectedSoundMode;
        set
        {
            if (Set(ref _selectedSoundMode, value)) OnModeSelected(value);
        }
    }
    private SoundModeVm? _selectedSoundMode;
    public List<BandVm> Bands { get; private set; } = [];
    public string[] SlopeOptions { get; } = ["-6", "-12", "-18", "-24"];
    public string[] SpaceOptions { get; } = ["0", "-1", "-2", "-3", "-4"];

    public RelayCommand MuteToggleCommand { get; }
    public RelayCommand ReconnectCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand AcousticResetCommand { get; }
    public RelayCommand ShowLogsCommand { get; }

    /// <summary>托盘"退出"回调，由 App 组装时注入。</summary>
    public Action? ExitRequested { get; set; }

    public MainViewModel(Dispatcher dispatcher, Mr5Profile profile, StateStore store,
        EdifierSession session, SettingsService settings)
    {
        _dispatcher = dispatcher;
        _profile = profile;
        _store = store;
        _session = session;
        _settings = settings;
        AutoStartEnabled = AutoStartService.IsEnabled();

        SoundModes = profile.SoundModes.Select(m => new SoundModeVm(m.Id, m.Label)).ToList();

        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(profile.Volume.ThrottleMs) };
        _volumeTimer.Tick += (_, _) =>
        {
            _volumeTimer.Stop();
            if (_pendingVolume >= 0)
            {
                _session.Enqueue(CommandFactory.SetVolume(_pendingVolume, VolumeMax));
                _pendingVolume = -1;
            }
        };

        _acousticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _acousticTimer.Tick += (_, _) => { _acousticTimer.Stop(); SendAcoustic(reset: false); };

        _bandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _bandTimer.Tick += (_, _) => { _bandTimer.Stop(); SendBandChange(); };

        MuteToggleCommand = new RelayCommand(ToggleMute, () => IsConnected);
        ReconnectCommand = new RelayCommand(() => _session.ForceRestart());
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        AcousticResetCommand = new RelayCommand(() => SendAcoustic(reset: true), () => HasAcoustic);
        ShowLogsCommand = new RelayCommand(() =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        });

        _store.StateChanged += s => _dispatcher.BeginInvoke(() => ApplyState(s));
    }

    private void ApplyState(SpeakerState s)
    {
        _suppressSync = true;
        try
        {
            IsConnected = s.Connected;
            DeviceName = s.DeviceName;
            StatusText = s.Connected ? "已连接" : "未连接";

            var firmware = s.Firmware is null ? "固件 —" : $"固件 {s.Firmware}";
            if (s.Rssi is { } rssi)
            {
                var (label, _) = SignalQuality.FromRssi(rssi, _profile.SignalBarsDbm);
                SignalLine = $"信号{label}（{rssi} dBm）· {firmware}";
            }
            else
            {
                SignalLine = $"信号 — · {firmware}";
            }

            Volume = s.Volume;
            VolumeMax = s.VolumeMax;
            IsMuted = s.IsMuted;

            InputLabel = s.InputId is { } input
                ? _profile.LabelForInput(input) ?? $"0x{input:X2}"
                : "—";

            if (s.DecodeCode is { } code)
            {
                DecodeLabel = _profile.LabelForDecode(code) ?? $"0x{code:X2}";
                HasDecode = true;
            }
            else
            {
                DecodeLabel = "";
                HasDecode = false;
            }

            if (s.SoundMode is { } mode)
                SelectedSoundMode = SoundModes.FirstOrDefault(m => m.Id == mode);
            if (s.CustomEq is { } eq && !ReferenceEquals(eq, _eqMirror))
                RebuildBands(eq);

            if (s.Acoustic is { } acoustic)
            {
                _acousticMirror = acoustic;
                LowCutoffHz = acoustic.LowCutoffHz;
                SlopeIndex = Math.Min((int)acoustic.LowCutoffSlope, 3);
                SpaceIndex = Math.Min((int)acoustic.AcousticSpace, 4);
                DesktopOn = acoustic.DesktopControl == 1;
                HasAcoustic = true;
            }
        }
        finally
        {
            _suppressSync = false;
        }
        Raise(string.Empty); // 全量刷新绑定
    }

    private void RebuildBands(CustomEqState eq)
    {
        var range = _profile.CustomEq.UiRangeDb;
        var freqs = _profile.CustomEq.FrequenciesHz;
        if (Bands.Count == 0)
        {
            Bands = eq.Bands
                .Where(b => b.Index < freqs.Length)
                .Select(b => new BandVm(
                    b.Index, freqs[b.Index], CodeToDb(b.GainCode),
                    range[0], range[1], OnBandGainChanged))
                .ToList();
            HasEq = true;
            Raise(nameof(Bands));
        }
        else
        {
            foreach (var vm in Bands)
            {
                var match = eq.Bands.FirstOrDefault(b => b.Index == vm.Index);
                if (match is not null) vm.SetGainSilently(CodeToDb(match.GainCode));
            }
        }
        _eqMirror = eq;
    }

    private double CodeToDb(byte code) =>
        (code - _profile.EffectiveZeroGainCode) * _profile.EffectiveDbPerCode;

    private byte DbToCode(double db)
    {
        var code = (int)Math.Round(_profile.EffectiveZeroGainCode + db / _profile.EffectiveDbPerCode);
        return (byte)Math.Clamp(code, 0, 12);
    }

    // ---- UI → 命令队列 ----

    public void SetVolumeFromUi(double value)
    {
        if (!IsConnected) return;
        var v = (int)Math.Round(value);
        if (v > 0) _lastNonZeroVolume = v;
        if (v != Volume) _store.Apply(s => s with { Volume = v }); // 乐观更新，推送校正
        _pendingVolume = v;
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    private void ToggleMute()
    {
        if (!IsConnected) return;
        if (!IsMuted && Volume > 0) _lastNonZeroVolume = Volume;
        var target = IsMuted ? _lastNonZeroVolume : 0;
        _store.Apply(s => s with { Volume = target });
        _session.Enqueue(CommandFactory.SetVolume(target, VolumeMax));
    }

    private void OnModeSelected(SoundModeVm vm)
    {
        if (_suppressSync || !IsConnected || vm.Id is not { } id) return;
        _store.Apply(s => s with { SoundMode = id });
        _session.Enqueue(CommandFactory.SetSoundMode(id));
    }

    public void OnBandGainChanged(BandVm band)
    {
        if (_suppressSync || !IsConnected) return;
        _bandTimer.Stop();
        _bandTimer.Start();
    }

    private void SendBandChange()
    {
        if (!IsConnected || _eqMirror is null) return;
        var previous = _eqMirror;
        var bands = previous.Bands
            .Select(b => new CustomEqBand
            {
                Index = b.Index,
                FrequencyHz = b.FrequencyHz,
                GainCode = DbToCode(Bands.FirstOrDefault(vm => vm.Index == b.Index)?.GainDb
                           ?? CodeToDb(b.GainCode)),
            })
            .ToList();
        var updated = new CustomEqState
        {
            EqIndex = previous.EqIndex,
            Byte0 = previous.Byte0,
            Bands = bands,
            DateBytes = previous.DateBytes,
            Name = previous.Name,
        };
        _eqMirror = updated;
        _store.Apply(s => s with { CustomEq = updated });
        for (var i = 0; i < bands.Count; i++)
        {
            if (bands[i].GainCode != previous.Bands[i].GainCode)
                _session.Enqueue(CommandFactory.SetCustomEqBand(updated, i));
        }
    }

    public void OnAcousticChanged()
    {
        if (_suppressSync || !IsConnected) return;
        _acousticTimer.Stop();
        _acousticTimer.Start();
    }

    private void SendAcoustic(bool reset)
    {
        if (!IsConnected) return;
        var previous = _acousticMirror
            ?? new AcousticTuning(
                (byte)(SelectedSoundMode?.Id ?? 0), 1, 0, 40, 1, 0, 0);
        var tuning = new AcousticTuning(
            previous.Mode,
            previous.Index,
            reset ? AcousticTuning.ResetFlag : previous.Byte0,
            (byte)Math.Clamp((int)LowCutoffHz, 20, 100),
            (byte)Math.Clamp(SlopeIndex, 0, 3),
            (byte)Math.Clamp(SpaceIndex, 0, 4),
            DesktopOn ? (byte)1 : (byte)0);
        _acousticMirror = tuning;
        _store.Apply(s => s with { Acoustic = tuning });
        _session.Enqueue(CommandFactory.SetAcoustic(tuning));
    }

    public void ToggleAutoStart(bool enabled)
    {
        try
        {
            AutoStartService.Set(enabled);
            AutoStartEnabled = enabled;
            Raise(nameof(AutoStartEnabled));
            _settings.AutoStart = enabled;
            _settings.Save();
        }
        catch (Exception ex)
        {
            FileLogger.Error("切换开机自启失败", ex);
        }
    }
}
