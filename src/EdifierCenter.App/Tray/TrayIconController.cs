using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EdifierCenter.App.Infrastructure;
using EdifierCenter.App.Services;
using EdifierCenter.App.UI;
using EdifierCenter.App.ViewModels;
using EdifierCenter.Ble;
using EdifierCenter.Core;

namespace EdifierCenter.App.Tray;

/// <summary>
/// 托盘图标（Shell_NotifyIcon 原生实现）：应用图标微缩 + 状态点；
/// 左键弹 flyout、右键菜单、悬停显示状态摘要（docs/ui.html §02/§04）。
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const uint Id = 1;

    private readonly MainViewModel _vm;
    private readonly FlyoutWindow _flyout;
    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher;
    private Icon? _icon;
    private TrayIconState _lastState = TrayIconState.Reconnecting;
    private volatile bool _reconnecting = true;
    private bool _added;

    public TrayIconController(MainViewModel vm, EdifierSession session, FlyoutWindow flyout)
    {
        _vm = vm;
        _flyout = flyout;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var parameters = new HwndSourceParameters("EdifierCenterTrayWindow");
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        vm.PropertyChanged += (_, _) => Update();
        session.LinkStateChanged += state =>
        {
            _reconnecting = state is LinkState.Scanning or LinkState.Connecting or LinkState.Reconnecting;
            _dispatcher.BeginInvoke(Update);
        };

        Add();
        Update();
    }

    private void Add()
    {
        var data = NativeMethods.NotifyIconData.Create(_source.Handle, Id,
            NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        data.uCallbackMessage = CallbackMessage;
        ApplyIcon(ref data);
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_added) FileLogger.Error("托盘图标注册失败");
    }

    private void Update()
    {
        if (!_added) return;
        if (CurrentState() == _lastState && _icon is not null) return;
        var data = NativeMethods.NotifyIconData.Create(_source.Handle, Id,
            NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        data.uCallbackMessage = CallbackMessage;
        ApplyIcon(ref data);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private TrayIconState CurrentState() =>
        _reconnecting
            ? TrayIconState.Reconnecting
            : !_vm.IsConnected ? TrayIconState.Disconnected
            : _vm.IsMuted ? TrayIconState.Muted
            : TrayIconState.Connected;

    private void ApplyIcon(ref NativeMethods.NotifyIconData data)
    {
        var state = CurrentState();
        _lastState = state;
        if (_icon is { } old)
        {
            _ = NativeMethods.DestroyIcon(old.Handle);
            old.Dispose();
        }
        _icon = TrayIconFactory.Create(state, 20);
        data.hIcon = _icon.Handle;
        data.szTip = _vm.IsConnected
            ? $"{_vm.DeviceName} · 已连接 · 音量 {_vm.Volume}/{_vm.VolumeMax}{(_vm.IsMuted ? " · 已静音" : "")}"
            : $"{_vm.DeviceName} · 未连接";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return IntPtr.Zero;
        handled = true;
        switch (lParam.ToInt32() & 0xFFFF)
        {
            case WmLButtonUp:
                if (_flyout.IsVisible) _flyout.Hide();
                else _flyout.ShowAtCursor();
                break;
            case WmRButtonUp:
                ShowMenu();
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var autoStartItem = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = _vm.AutoStartEnabled,
            Style = MenuItemStyle(),
        };
        autoStartItem.Click += (_, _) => _vm.ToggleAutoStart(autoStartItem.IsChecked);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Style = MenuStyle(),
            Items =
            {
                MenuItem("打开面板", new RelayCommand(() => _flyout.ShowAtCursor())),
                MenuItem("静音 / 取消静音", _vm.MuteToggleCommand),
                MenuItem("立即重连", _vm.ReconnectCommand),
                autoStartItem,
                new Separator(),
                MenuItem("打开日志文件夹", _vm.ShowLogsCommand),
                MenuItem("退出", _vm.ExitCommand),
            },
        };
        menu.IsOpen = true;
    }

    private static Style MenuStyle() => (Style)Application.Current.Resources["TrayMenu"];

    private static Style MenuItemStyle() => (Style)Application.Current.Resources["TrayMenuItem"];

    private static MenuItem MenuItem(string header, ICommand command) => new()
    {
        Header = header,
        Command = command,
        Style = MenuItemStyle(),
    };

    public void Dispose()
    {
        if (_added)
        {
            var data = NativeMethods.NotifyIconData.Create(_source.Handle, Id, 0);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        }
        if (_icon is { } icon)
        {
            _ = NativeMethods.DestroyIcon(icon.Handle);
            icon.Dispose();
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
