using System.Threading.Channels;
using EdifierCenter.Core;
using EdifierCenter.Core.Model;
using EdifierCenter.Core.Protocol;

namespace EdifierCenter.Ble;

public enum LinkState
{
    Idle,
    Scanning,
    Connecting,
    Ready,
    Reconnecting,
}

/// <summary>
/// 会话编排（docs/03 §5）：扫描 → 连接 → 仪式（订阅+首读）→ 就绪（写队列 + 轮询保活）→
/// 断线（指数退避重连）。单条长连接——per-command 重连对本机型的 BLE 外设不可靠（omarchy 实测）。
/// 线程模型：本类内部所有状态仅在会话任务上变更；对外的状态同步经 StateStore 原子交换。
/// </summary>
public sealed class EdifierSession : IAsyncDisposable
{
    private readonly Mr5Profile _profile;
    private readonly IEdifierTransport _transport;
    private readonly StateStore _store;
    private readonly Channel<byte[]> _queue =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly List<byte[]> _recentFrames = [];
    private readonly object _frameGate = new();
    private readonly TaskCompletionSource _restart =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<LinkState>? LinkStateChanged;
    public LinkState State { get; private set; } = LinkState.Idle;

    public EdifierSession(Mr5Profile profile, IEdifierTransport transport, StateStore store)
    {
        _profile = profile;
        _transport = transport;
        _store = store;
        _transport.Notified += OnNotified;
        _transport.RssiChanged += rssi => _store.Apply(s => s with { Rssi = rssi });
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is { } loop)
        {
            try { await loop; } catch { /* 取消路径 */ }
        }
        _loop = null;
        State = LinkState.Idle;
        LinkStateChanged?.Invoke(State);
    }

    /// <summary>立即重连（睡眠唤醒 / 用户触发）。会话任务会尽快打断当前等待。</summary>
    public void ForceRestart() => _restart.TrySetResult();

    public void Enqueue(byte[] frame) => _queue.Writer.TryWrite(frame);

    public void Enqueue(CommandId id) => _queue.Writer.TryWrite(CommandFactory.Query(id));

    private void SetState(LinkState state)
    {
        if (State == state) return;
        State = state;
        LinkStateChanged?.Invoke(state);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoffIndex = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(backoffIndex == 0 ? LinkState.Scanning : LinkState.Reconnecting);
                var address = await _transport.FindFirstAsync(_profile.Discovery.SearchServiceUuids, ct);
                SetState(LinkState.Connecting);
                await _transport.ConnectAsync(address, _profile, ct);
                backoffIndex = 0;
                SetState(LinkState.Ready);
                _store.Apply(s => s with
                {
                    Connected = true,
                    Address = address,
                    DeviceName = _transport.DeviceName ?? s.DeviceName,
                });
                await RunReadyAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"会话中断: {ex.Message}");
            }
            finally
            {
                _store.Apply(s => s.Connected ? s with { Connected = false } : s);
            }
            await OnDisconnectedAsync();
            var delaySeconds = _profile.Session.ReconnectBackoffSeconds[
                Math.Min(backoffIndex, _profile.Session.ReconnectBackoffSeconds.Length - 1)];
            backoffIndex++;
            Log($"{delaySeconds}s 后重连（第 {backoffIndex} 次退避）");
            try
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                var completed = await Task.WhenAny(delay, _restart.Task);
                if (completed != delay) backoffIndex = 0; // 手动重连不计退避
                _restart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>就绪态：消费写队列 + 轮询保活，直到断线 / 唤醒重启 / 取消。</summary>
    private async Task RunReadyAsync(CancellationToken ct)
    {
        _restart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 断线 / 手动重连时终止就绪期子任务（写循环、轮询）
        var disconnectReg = _transport.DisconnectedTask.ContinueWith(
            _ => readyCts.Cancel(), TaskScheduler.Default);
        var restartReg = _restart.Task.ContinueWith(
            _ => readyCts.Cancel(), TaskScheduler.Default);

        try
        {
            // 连接仪式第 2 步：首读（docs/02 §4）
            foreach (var name in _profile.Session.InitialRead)
            {
                var id = MapInitialRead(name);
                if (id.HasValue) Enqueue(id.Value);
            }
            Enqueue(CommandId.GetDecodeStatus);
            Enqueue(CommandId.GetInput);

            var pollTask = PollLoopAsync(readyCts.Token);
            var writerTask = WriterLoopAsync(readyCts.Token);
            var completed = await Task.WhenAny(
                pollTask,
                writerTask,
                _transport.DisconnectedTask,
                _restart.Task);
            ct.ThrowIfCancellationRequested();
            if (completed == _restart.Task) throw new IOException("手动重连");
            // 断线 / 写失败等：异常上抛交由 RunAsync 统一走断线流程
            await completed;
        }
        finally
        {
            readyCts.Cancel();
            try { await disconnectReg; } catch { }
            try { await restartReg; } catch { }
        }
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            while (_queue.Reader.TryRead(out var frame))
            {
                await _transport.WriteAsync(frame, ct);
                Log($"→ {Convert.ToHexString(frame)}");
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_profile.Session.PollIntervalSeconds));
        while (await timer.WaitForNextTickAsync(ct))
        {
            Enqueue(CommandId.GetVolume);
            Enqueue(CommandId.GetEq);
            Enqueue(CommandId.GetDecodeStatus);
            Enqueue(CommandId.GetInput);
        }
    }

    private void OnNotified(byte[] data)
    {
        if (!FrameCodec.TryParse(data, out var frame)) return;
        lock (_frameGate)
        {
            _recentFrames.Add(data);
            if (_recentFrames.Count > 200) _recentFrames.RemoveAt(0);
        }
        if (!frame.ChecksumValid)
        {
            Log($"丢弃校验和错误的帧: {Convert.ToHexString(data)}");
            return;
        }
        if (!ResponseParser.TryParse(frame, out var update)) return;
        switch (update.Kind)
        {
            case UpdateKind.Volume:
                _store.Apply(s => s with
                {
                    Volume = update.Volume >= 0 ? update.Volume : s.Volume,
                    VolumeMax = update.VolumeMax > 0 ? update.VolumeMax : s.VolumeMax,
                });
                break;
            case UpdateKind.Input:
                _store.Apply(s => s with { InputId = update.InputId });
                break;
            case UpdateKind.SoundMode:
                _store.Apply(s => s with { SoundMode = update.SoundMode });
                break;
            case UpdateKind.Acoustic:
                _store.Apply(s => s with
                {
                    SoundMode = update.Acoustic!.Value.Mode,
                    Acoustic = update.Acoustic,
                });
                break;
            case UpdateKind.CustomEq:
                _store.Apply(s => s with { CustomEq = update.CustomEq });
                break;
            case UpdateKind.Version:
                _store.Apply(s => s with { Firmware = update.Version });
                break;
            case UpdateKind.DecodeStatus:
                _store.Apply(s => s with { DecodeCode = update.DecodeCode });
                break;
            case UpdateKind.DeviceName:
                _store.Apply(s => s with { DeviceName = string.IsNullOrWhiteSpace(update.Name) ? s.DeviceName : update.Name });
                break;
        }
        if (update.Kind != UpdateKind.Ack)
            Log($"← {frame.Type:X2} {(CommandId)frame.Command} {update.PayloadHex}");
    }

    private CommandId? MapInitialRead(string name) => name switch
    {
        "support" => CommandId.GetSupport,
        "volume" => CommandId.GetVolume,
        "eq" => CommandId.GetEq,
        "customEq" => CommandId.GetCustomEq,
        "version" => CommandId.GetVersion,
        "state" => CommandId.GetDeviceState,
        "input" => CommandId.GetInput,
        "decode" => CommandId.GetDecodeStatus,
        "name" => CommandId.GetDeviceName,
        _ => null,
    };

    /// <summary>导出诊断信息到运行目录 diagnostics\last-session.json（用户可见的调试数据）。</summary>
    private void WriteDiagnostics(string reason)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            byte[] frames;
            lock (_frameGate) frames = [.. _recentFrames.SelectMany(f => f)];
            var snapshot = _store.Current;
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                reason,
                state = new
                {
                    snapshot.Connected,
                    snapshot.DeviceName,
                    snapshot.Firmware,
                    snapshot.Rssi,
                    snapshot.Volume,
                    snapshot.VolumeMax,
                    snapshot.InputId,
                    snapshot.SoundMode,
                    snapshot.DecodeCode,
                },
                recentFrames = Convert.ToHexString(frames),
            });
            File.WriteAllText(Path.Combine(dir, "last-session.json"), payload);
        }
        catch { /* 诊断尽力而为 */ }
    }

    private async Task OnDisconnectedAsync()
    {
        WriteDiagnostics("disconnected");
        SetState(LinkState.Reconnecting);
        await Task.CompletedTask;
    }

    private void Log(string message) => FileLogger.Info($"[会话] {message}");

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _transport.DisposeAsync();
    }
}
