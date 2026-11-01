using System.IO;
using EdifierCenter.Core.Model;
using Microsoft.Win32;

namespace EdifierCenter.App.Services;

/// <summary>设置（可移植：config.json 位于 exe 运行目录）。</summary>
public sealed class SettingsService
{
    private string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public ulong? LastDeviceAddress { get; set; }
    public bool AutoStart { get; set; }

    /// <summary>首次启动校准向导的产出（输入标签/声音模式 ID/EQ 增益换算），docs/03 §8。</summary>
    public CalibrationData Calibration { get; set; } = new();

    private static string GetFilePath() => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static SettingsService Load()
    {
        try
        {
            var path = GetFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<SettingsService>(json) ?? new SettingsService();
            }
        }
        catch { /* 损坏则用默认值 */ }
        return new SettingsService();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(GetFilePath(),
                System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
        }
        catch (Exception ex)
        {
            Core.FileLogger.Error("保存设置失败", ex);
        }
    }
}

/// <summary>开机自启：HKCU Run（任务管理器 → 启动应用 中可见/可关，符合 Windows 规范）。</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdifierCenter";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) throw new InvalidOperationException("无法打开注册表 Run 键");
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
