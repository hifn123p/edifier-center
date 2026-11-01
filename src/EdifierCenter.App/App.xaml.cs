using System.Reflection;
using System.Windows;
using EdifierCenter.App.Infrastructure;
using EdifierCenter.App.Services;
using EdifierCenter.App.Tray;
using EdifierCenter.App.UI;
using EdifierCenter.App.ViewModels;
using EdifierCenter.Ble;
using EdifierCenter.Core;
using EdifierCenter.Core.Model;
using Microsoft.Win32;

namespace EdifierCenter.App;

public partial class App : Application
{
    /// <summary>供 FlyoutWindow 设置 DWM 深色 Mica。</summary>
    public static bool IsDarkTheme { get; private set; }

    private Mutex? _mutex;
    private StateStore? _store;
    private EdifierSession? _session;
    private TrayIconController? _tray;
    private SettingsService? _settings;
    private Mr5Profile? _profile;
    private MainViewModel? _mainVm;
    private bool _wizardShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!SingleInstance.Acquire())
        {
            FileLogger.Info("已有实例在运行，退出。");
            Shutdown();
            return;
        }

        FileLogger.Init(AppContext.BaseDirectory);
        ApplyThemeFromSystem();
        DispatcherUnhandledException += (_, args) =>
        {
            FileLogger.Error("UI 未处理异常", args.Exception);
            args.Handled = true;
        };

        var profile = Mr5Profile.LoadEmbedded(name =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream(name));
        var settings = SettingsService.Load();
        _settings = settings;
        _profile = profile;

        // 应用已持久化的校准结果（首次运行为空操作，连接后就绪后会弹向导）
        profile.ApplyCalibration(settings.Calibration);

        _store = new StateStore();
        var transport = new WinRtTransport();
        _session = new EdifierSession(profile, transport, _store);
        _session.LinkStateChanged += state =>
        {
            FileLogger.Info($"[链路] {state}");
            if (state == LinkState.Ready && !settings.Calibration.Done && !_wizardShown)
            {
                _wizardShown = true;
                Dispatcher.BeginInvoke(ShowCalibration);
            }
        };

        var vm = new MainViewModel(Dispatcher, profile, _store, _session, settings)
        {
            ExitRequested = () =>
            {
                _tray?.Dispose();
                Shutdown();
            },
        };
        _mainVm = vm;
        vm.ShowCalibrationRequested = ShowCalibration;
        var flyout = new FlyoutWindow(vm);
        _tray = new TrayIconController(vm, _session, flyout);

        SystemEvents.PowerModeChanged += (_, args) =>
        {
            if (args.Mode == PowerModes.Resume)
            {
                FileLogger.Info("系统唤醒，触发重连");
                _session.ForceRestart();
            }
        };

        _session.Start();
        FileLogger.Info("启动完成（托盘就绪）。");
    }

    /// <summary>打开校准向导（首次连接自动触发；托盘菜单可随时重跑）。</summary>
    private void ShowCalibration()
    {
        if (_profile is null || _store is null || _session is null || _settings is null) return;
        FileLogger.Info("打开校准向导");
        var calibrationVm = new CalibrationViewModel(Dispatcher, _profile, _store, _session, _settings);
        var window = new CalibrationWindow(calibrationVm);
        calibrationVm.Completed += () =>
        {
            // 校准结果已写入 config.json 并覆盖 profile；刷新面板的标签显示
            _mainVm?.RefreshFromStore();
        };
        window.Show();
    }

    private void ApplyThemeFromSystem()
    {
        bool light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = (key?.GetValue("AppsUseLightTheme") as int? ?? 1) == 1;
        }
        catch { /* 读取失败按亮色 */ }
        IsDarkTheme = !light;
        var source = new Uri($"pack://application:,,,/Themes/{(light ? "Light" : "Dark")}.xaml");
        Resources.MergedDictionaries.Insert(0, new ResourceDictionary { Source = source });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            FileLogger.Error("会话停止异常", ex);
        }
        SingleInstance.Release();
        base.OnExit(e);
    }
}
