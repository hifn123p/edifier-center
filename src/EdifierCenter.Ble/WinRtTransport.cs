using EdifierCenter.Core.Model;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace EdifierCenter.Ble;

/// <summary>
/// Windows Runtime BLE 传输（与 EdifierControl 同一套系统 API，无第三方依赖）。
/// GATT 读写一律 Uncached（规避 Windows GATT 缓存导致的 Unreachable）。
/// </summary>
public sealed class WinRtTransport : IEdifierTransport
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEAdvertisementWatcher? _rssiWatcher;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _notify;
    private GattCharacteristic? _write;
    private ulong _address;
    private TaskCompletionSource _disconnectTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disconnectRaised;

    public event Action<byte[]>? Notified;
    public event Action? Disconnected;
    public event Action<short>? RssiChanged;
    public string? DeviceName => _device?.Name is { Length: > 0 } name ? name : null;
    public Task DisconnectedTask => _disconnectTcs.Task;

    public async Task<ulong> FindFirstAsync(IReadOnlyList<string> searchServiceUuids, CancellationToken ct)
    {
        var uuids = searchServiceUuids.Select(Guid.Parse).ToArray();
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        void OnReceived(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // 匹配策略（MR5BT 实测：广播只带 00000a03 且名字为 EDIFIER BLE）：
            // ① 广播服务 UUID 命中 profile 列表；② 厂商 ID = 0x07E0（Edifier）；
            // ③ 广播名 = EDIFIER BLE。任一命中即认。
            foreach (var uuid in args.Advertisement.ServiceUuids)
            {
                if (uuids.Contains(uuid))
                {
                    tcs.TrySetResult(args.BluetoothAddress);
                    return;
                }
            }
            foreach (var section in args.Advertisement.ManufacturerSections)
            {
                if (section.CompanyId == 0x07E0)
                {
                    tcs.TrySetResult(args.BluetoothAddress);
                    return;
                }
            }
            if (string.Equals(args.Advertisement.LocalName, "EDIFIER BLE", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(args.BluetoothAddress);
            }
        }
        watcher.Received += new Windows.Foundation.TypedEventHandler<
            BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>(OnReceived);
        watcher.Start();
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct));
            if (completed != tcs.Task)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("扫描未发现目标设备");
            }
            return tcs.Task.Result;
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= new Windows.Foundation.TypedEventHandler<
                BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>(OnReceived);
        }
    }

    public async Task ConnectAsync(ulong address, Mr5Profile profile, CancellationToken ct)
    {
        _address = address;
        _disconnectRaised = false;
        _disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new IOException($"无法连接 BLE 设备 {address:X12}");

        // 依次尝试 profile 中的服务 UUID（MR5BT=0a03 / 标准版=3a01 / 8625 改版=7e03），
        // 第一个能取到特征码的即为该型号的控制服务。
        GattCharacteristic? notify = null;
        GattCharacteristic? write = null;
        GattDeviceService? controlService = null;
        foreach (var serviceUuidString in profile.Discovery.ServiceUuids)
        {
            var serviceUuid = Guid.Parse(serviceUuidString);
            var services = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                continue;
            var service = services.Services[0];
            var notifyResult = await service.GetCharacteristicsForUuidAsync(
                Guid.Parse(profile.Discovery.NotifyCharacteristicUuid), BluetoothCacheMode.Uncached);
            var writeResult = await service.GetCharacteristicsForUuidAsync(
                Guid.Parse(profile.Discovery.WriteCharacteristicUuid), BluetoothCacheMode.Uncached);
            if (notifyResult.Status != GattCommunicationStatus.Success || notifyResult.Characteristics.Count == 0
                || writeResult.Status != GattCommunicationStatus.Success || writeResult.Characteristics.Count == 0)
                continue;
            controlService = service;
            notify = notifyResult.Characteristics[0];
            write = writeResult.Characteristics[0];
            break;
        }
        if (controlService is null || notify is null || write is null)
            throw new IOException("控制服务未暴露——请按音箱背面键进入配对模式后重试");

        _notify = notify;
        _notify.ValueChanged += new Windows.Foundation.TypedEventHandler<
            GattCharacteristic, GattValueChangedEventArgs>(OnNotify);

        var cccd = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccd != GattCommunicationStatus.Success)
            throw new IOException($"订阅通知失败（status={cccd}）");

        _write = write;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        StartRssiWatcher();
    }

    public async Task WriteAsync(byte[] frame, CancellationToken ct)
    {
        var write = _write ?? throw new InvalidOperationException("尚未连接");
        var buffer = CryptographicBuffer.CreateFromByteArray(frame);
        var status = await write.WriteValueAsync(buffer, GattWriteOption.WriteWithResponse);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"BLE 写入失败: {status}");
    }

    private void StartRssiWatcher()
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        _rssiWatcher = watcher;
        _rssiHandler = new Windows.Foundation.TypedEventHandler<
            BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>((w, args) =>
        {
            if (args.BluetoothAddress == _address) RssiChanged?.Invoke(args.RawSignalStrengthInDBm);
        });
        watcher.Received += _rssiHandler;
        try { watcher.Start(); } catch { /* RSSI 仅辅助，失败可忽略 */ }
    }

    private void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        Notified?.Invoke(data);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        RaiseDisconnected();
    }

    private void RaiseDisconnected()
    {
        if (_disconnectRaised) return;
        _disconnectRaised = true;
        Disconnected?.Invoke();
        _disconnectTcs.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        try { _watcher?.Stop(); } catch { }
        if (_rssiWatcher is { } rssi)
        {
            try { rssi.Stop(); } catch { }
            if (_rssiHandler is { } handler) try { rssi.Received -= handler; } catch { }
        }
        if (_device is { } device)
        {
            try { device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            if (_notify is { } notify)
            {
                try
                {
                    notify.ValueChanged -= new Windows.Foundation.TypedEventHandler<
                        GattCharacteristic, GattValueChangedEventArgs>(OnNotify);
                }
                catch { }
            }
        }
        RaiseDisconnected();
        if (_device is { } d) d.Dispose();
        _device = null;
        _notify = null;
        _write = null;
        await Task.CompletedTask;
    }

    private Windows.Foundation.TypedEventHandler<
        BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>? _rssiHandler;
}
