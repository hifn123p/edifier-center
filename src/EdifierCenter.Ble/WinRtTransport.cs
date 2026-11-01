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
            foreach (var uuid in args.Advertisement.ServiceUuids)
            {
                if (!uuids.Contains(uuid)) continue;
                tcs.TrySetResult(args.BluetoothAddress);
                break;
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

        var serviceUuid = Guid.Parse(profile.Discovery.ServiceUuids[0]);
        var services = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new IOException($"控制服务未暴露（status={services.Status}）——请按音箱背面键进入配对模式后重试");
        var service = services.Services[0];

        var notifyResult = await service.GetCharacteristicsForUuidAsync(
            Guid.Parse(profile.Discovery.NotifyCharacteristicUuid), BluetoothCacheMode.Uncached);
        if (notifyResult.Status != GattCommunicationStatus.Success || notifyResult.Characteristics.Count == 0)
            throw new IOException("通知特征不可用");
        _notify = notifyResult.Characteristics[0];
        _notify.ValueChanged += new Windows.Foundation.TypedEventHandler<
            GattCharacteristic, GattValueChangedEventArgs>(OnNotify);

        var cccd = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccd != GattCommunicationStatus.Success)
            throw new IOException($"订阅通知失败（status={cccd}）");

        var writeResult = await service.GetCharacteristicsForUuidAsync(
            Guid.Parse(profile.Discovery.WriteCharacteristicUuid), BluetoothCacheMode.Uncached);
        if (writeResult.Status != GattCommunicationStatus.Success || writeResult.Characteristics.Count == 0)
            throw new IOException("写入特征不可用");
        _write = writeResult.Characteristics[0];

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
