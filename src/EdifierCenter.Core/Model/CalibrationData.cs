using System.Text.Json.Serialization;

namespace EdifierCenter.Core.Model;

/// <summary>
/// 校准结果（由程序内"首次启动校准向导"产出，持久化到运行目录 config.json）。
/// </summary>
public sealed class CalibrationData
{
    /// <summary>是否已完成（完成过就不再自动弹向导；托盘菜单可重新运行）。</summary>
    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("calibratedAt")]
    public string CalibratedAt { get; set; } = "";

    /// <summary>输入源标签表：键 = 字节值十进制字符串，值 = 显示名（如 "1" → "XLR"）。</summary>
    [JsonPropertyName("inputLabels")]
    public Dictionary<string, string> InputLabels { get; set; } = [];

    /// <summary>声音模式 ID：键 = 模式名（监听/音乐/自定义），值 = 字节值十进制字符串。</summary>
    [JsonPropertyName("soundModeIds")]
    public Dictionary<string, string> SoundModeIds { get; set; } = [];

    /// <summary>自定义 EQ 增益 0dB 对应的码点（62Hz 波段在 App 中设为 0dB 时读到的字节）。</summary>
    [JsonPropertyName("eqGainCodeZeroDb")]
    public int? EqGainCodeZeroDb { get; set; }

    /// <summary>每码对应的 dB 数（由 0dB 与 +2dB 两个采样点推得；默认假设 0.5）。</summary>
    [JsonPropertyName("eqGainDbPerCode")]
    public double EqGainDbPerCode { get; set; } = 0.5;
}
