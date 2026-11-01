using System.Windows;
using System.Windows.Threading;
using EdifierCenter.App.Services;
using EdifierCenter.Ble;
using EdifierCenter.Core.Model;
using EdifierCenter.Core.Protocol;

namespace EdifierCenter.App.ViewModels;

/// <summary>
/// 首次启动校准向导（docs/03 §9）：在程序内完成原 Phase 0 的标定——
/// 输入源标签、EQ 增益 0dB/+2dB 码点、声音模式 ID；结果写入运行目录 config.json。
/// 全部步骤可跳过（跳过后运行时回退内置假设/原始字节显示）。
/// </summary>
public sealed class CalibrationViewModel : BindBase
{
    private sealed record Step(string Kind, string Label, string Title, string Instruction, bool NeedsName);

    private readonly StateStore _store;
    private readonly EdifierSession _session;
    private readonly Mr5Profile _profile;
    private readonly SettingsService _settings;
    private readonly List<Step> _steps;
    private readonly Dictionary<string, string> _inputLabels = [];
    private readonly Dictionary<string, string> _modeIds = [];
    private int _index = -1;
    private int? _eqZeroCode;
    private double? _eqDbPerCode;
    private bool _busy;

    public string StepCounter { get; private set; } = "";
    public string StepTitle { get; private set; } = "准备中";
    public string StepInstruction { get; private set; } = "";
    public bool NameVisible { get; private set; }
    public string NameValue { get; set; } = "";
    public bool Busy { get; private set; }
    public bool Finished { get; private set; }
    public string LiveText { get; private set; } = "";
    public string Summary { get; private set; } = "";

    public Visibility WizardVisibility => Finished ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FinishedVisibility => Finished ? Visibility.Visible : Visibility.Collapsed;

    public RelayCommand NextCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand SkipAllCommand { get; }

    /// <summary>向导完成（结果已写入 config.json 并应用到 profile）。</summary>
    public event Action? Completed;

    public CalibrationViewModel(Dispatcher dispatcher, Mr5Profile profile,
        StateStore store, EdifierSession session, SettingsService settings)
    {
        _dispatcher = dispatcher;
        _profile = profile;
        _store = store;
        _session = session;
        _ = settings;

        _steps =
        [
            new("input", "XLR", "输入源：XLR",
                "请在手机 App 中把音箱输入切换到 XLR（卡侬输入），等 2~3 秒后点「下一步」，程序会记录它对应的字节值。", false),
            new("input", "蓝牙", "输入源：蓝牙",
                "请在 App 中把输入切换到「蓝牙」，然后点「下一步」。", false),
            new("input", "其他", "输入源：其他（可选）",
                "如果音箱还有 AUX / 光纤等输入且想显示名称：切到该输入，在下方填入名称，点「下一步」。不需要就点「跳过此项」。", true),
            new("eq0", "", "自定义 EQ：0dB 码点",
                "请在 App 的「自定义 EQ」中把第一个波段（62Hz）调到 0dB，然后点「下一步」，程序会读取它对应的码点。", false),
            new("eq2", "", "自定义 EQ：+2dB 验证",
                "再把 62Hz 调到 +2.0dB，点「下一步」（用于验证每个 0.5dB 对应的码距）。", false),
            new("mode", "监听", "声音模式：监听",
                "请在 App 中把声音模式切到「监听」，点「下一步」。", false),
            new("mode", "音乐", "声音模式：音乐",
                "切到「音乐」，点「下一步」。", false),
            new("mode", "自定义", "声音模式：自定义",
                "切到「自定义」，点「下一步」。", false),
        ];

        NextCommand = new RelayCommand(async () => await NextAsync(), () => !_busy);
        SkipCommand = new RelayCommand(async () => await AdvanceAsync(), () => !_busy);
        SkipAllCommand = new RelayCommand(Finish, () => !_busy);

        _store.StateChanged += s => _dispatcher.BeginInvoke(UpdateLive);
        _ = AdvanceAsync();
        UpdateLive();
    }

    private async Task NextAsync()
    {
        if (_busy || _index < 0 || _index >= _steps.Count) return;
        _busy = true;
        Raise(nameof(Busy));
        try
        {
            var step = _steps[_index];
            switch (step.Kind)
            {
                case "input":
                    if (_store.Current.InputId is { } b)
                    {
                        var label = step.Label == "其他"
                            ? (string.IsNullOrWhiteSpace(NameValue) ? $"输入 {b}" : NameValue.Trim())
                            : step.Label;
                        _inputLabels[b.ToString()] = label;
                    }
                    break;

                case "eq0":
                    _session.Enqueue(CommandId.GetCustomEq);
                    await Task.Delay(900);
                    _eqZeroCode = _store.Current.CustomEq?.Bands.FirstOrDefault(x => x.Index == 0)?.GainCode;
                    break;

                case "eq2":
                    _session.Enqueue(CommandId.GetCustomEq);
                    await Task.Delay(900);
                    var code2 = _store.Current.CustomEq?.Bands.FirstOrDefault(x => x.Index == 0)?.GainCode;
                    if (_eqZeroCode is { } zero && code2 is { } c2 && c2 != zero)
                    {
                        // +2dB = |code2 − zero| 个码距 → 每 1 码的 dB 数
                        _eqDbPerCode = Math.Clamp(2.0 / Math.Abs(c2 - zero), 0.1, 4.0);
                    }
                    break;

                case "mode":
                    if (_store.Current.SoundMode is { } m)
                        _modeIds[step.Label] = m.ToString();
                    break;
            }
        }
        finally
        {
            _busy = false;
            Raise(nameof(Busy));
        }
        await AdvanceAsync();
    }

    private async Task AdvanceAsync()
    {
        _index++;
        if (_index >= _steps.Count)
        {
            Finish();
            return;
        }
        var step = _steps[_index];
        StepCounter = $"第 {_index + 1} / {_steps.Count} 步";
        StepTitle = step.Title;
        StepInstruction = step.Instruction;
        NameVisible = step.NeedsName;
        if (step.Kind is "eq0" or "eq2")
            _session.Enqueue(CommandId.GetCustomEq); // 预读当前曲线
        Raise(nameof(StepCounter));
        Raise(nameof(StepTitle));
        Raise(nameof(StepInstruction));
        Raise(nameof(NameVisible));
        await Task.CompletedTask;
    }

    private void Finish()
    {
        var calibration = _settings.Calibration;
        calibration.Done = true;
        calibration.CalibratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        calibration.InputLabels = _inputLabels;
        calibration.SoundModeIds = _modeIds;
        if (_eqZeroCode is { } zero) calibration.EqGainCodeZeroDb = zero;
        if (_eqDbPerCode is { } per) calibration.EqGainDbPerCode = per;
        _settings.Save();
        _profile.ApplyCalibration(calibration);
        Finished = true;
        Summary =
            $"输入标签：{_inputLabels.Count} 项\n" +
            $"声音模式 ID：{_modeIds.Count} 项\n" +
            $"EQ 增益：0dB = 码 {calibration.EqGainCodeZeroDb?.ToString() ?? "默认 6"}，"
            + $"{calibration.EqGainDbPerCode:0.##}dB/码\n\n" +
            "结果已写入运行目录 config.json。可随时从托盘菜单重新运行向导。";
        Raise(nameof(Finished));
        Raise(nameof(Summary));
        Completed?.Invoke();
    }

    private void UpdateLive()
    {
        var s = _store.Current;
        LiveText =
            $"连接：{(s.Connected ? "已连接" : "未连接")}    " +
            $"输入字节：{(s.InputId is { } i ? "0x" + i.ToString("X2") : "—")}    " +
            $"模式字节：{(s.SoundMode is { } m ? "0x" + m.ToString("X2") : "—")}";
        Raise(nameof(LiveText));
    }
}
