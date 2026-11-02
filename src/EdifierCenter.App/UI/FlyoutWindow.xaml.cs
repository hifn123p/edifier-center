using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using EdifierCenter.App.Infrastructure;
using EdifierCenter.App.ViewModels;

namespace EdifierCenter.App.UI;

/// <summary>
/// 托盘飞出面板（docs/ui.html §03）：DWM Mica 背景 + 圆角；失焦/Esc 自动关闭；
/// 打开动画 150ms scale+fade（仅 transform/opacity）。
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _prepared;

    public FlyoutWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch { /* 图标加载失败不影响功能 */ }
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
        };
        VolumeSlider.ValueChanged += (_, args) => _vm.SetVolumeFromUi(args.NewValue);
    }

    public new bool IsVisible => Visibility == Visibility.Visible;

    public new void Hide() => Visibility = Visibility.Hidden;

    public void ShowAtCursor()
    {
        Prepare();
        NativeMethods.GetCursorPos(out var point);
        var workArea = SystemParameters.WorkArea;
        var width = double.IsNaN(Width) ? 380 : Width;
        var height = double.IsNaN(ActualHeight) || ActualHeight < 100 ? 560 : ActualHeight;
        Left = Math.Min(Math.Max(point.X - width + 24, workArea.Left), workArea.Right - width - 4);
        Top = Math.Min(Math.Max(point.Y + 8, workArea.Top), workArea.Bottom - height - 4);
        Visibility = Visibility.Visible;
        Show();
        Activate();
    }

    private void Prepare()
    {
        if (_prepared) return;
        _prepared = true;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var dark = App.IsDarkTheme ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        var backdrop = NativeMethods.DWMSBT_MICA;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
        var corner = NativeMethods.DWMCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
        var margins = NativeMethods.Margins.FullSheet;
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }
}
