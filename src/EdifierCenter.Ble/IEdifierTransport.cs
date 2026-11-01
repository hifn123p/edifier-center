using EdifierCenter.Core.Model;

namespace EdifierCenter.Ble;

/// <summary>
/// BLE 传输抽象——隔离 WinRT 细节，供会话层与测试使用。
/// 实现约定：ConnectAsync 完成后 Notified 可用；链路断开触发 Disconnected（仅一次）。
/// </summary>
public interface IEdifierTransport : IAsyncDisposable
{
    /// <summary>按广播中的服务 UUID 扫描，返回第一个命中地址（同时匹配标准版/8625 改版）。</summary>
    Task<ulong> FindFirstAsync(IReadOnlyList<string> searchServiceUuids, CancellationToken ct);

    /// <summary>打开 GATT、订阅通知（连接仪式的第 1 步）。失败抛异常（服务未暴露等）。</summary>
    Task ConnectAsync(ulong address, Mr5Profile profile, CancellationToken ct);

    Task WriteAsync(byte[] frame, CancellationToken ct);

    /// <summary>音箱 → 主机 的原始帧。</summary>
    event Action<byte[]>? Notified;

    /// <summary>链路断开（音箱关机 / 超范围 / 连接丢失）。</summary>
    event Action? Disconnected;

    /// <summary>RSSI 变化（被动扫描，仅连接期间）。</summary>
    event Action<short>? RssiChanged;

    string? DeviceName { get; }
}
