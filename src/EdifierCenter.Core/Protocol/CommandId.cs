namespace EdifierCenter.Core.Protocol;

/// <summary>
/// 命令白名单（docs/02 §5）。危险命令（0x07 复位 / 0xCE 关机 / 0xCD 断开蓝牙 /
/// 0x45/0x46 EQ 存储 / OTA / 恢复出厂）不在此枚举，也不存在对应代码路径。
/// </summary>
public enum CommandId : byte
{
    GetCustomEq = 0x43,
    SetCustomEqBand = 0x44,
    GetInput = 0x61,
    GetVolume = 0x66,
    SetVolume = 0x67,
    GetDecodeStatus = 0x68,
    SetEq = 0xC4,
    GetVersion = 0xC6,
    GetDeviceName = 0xC9,
    GetEq = 0xD5,
    GetSupport = 0xD8,
    GetDeviceState = 0xF2,
}
