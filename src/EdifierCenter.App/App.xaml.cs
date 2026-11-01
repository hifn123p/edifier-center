using System.Reflection;
using System.Windows;
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
        _store = new StateStore();
        var transport = new WinRtTransport();
        _session = new EdifierSession(profile, transport, _store);
        _session.LinkStateChanged += state => FileLogger.Info($"[链路] {state}");

        var vm = new MainViewModel(Dispatcher, profile, _store, _session, settings)
        {
            ExitRequested = () =>
            {
                _tray?.Dispose();
                Shutdown();
            },
        };
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
