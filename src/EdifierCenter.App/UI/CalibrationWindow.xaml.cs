using System.Windows;
using EdifierCenter.App.ViewModels;

namespace EdifierCenter.App.UI;

/// <summary>首次启动校准向导窗口（结果写入运行目录 config.json）。</summary>
public partial class CalibrationWindow : Window
{
    public CalibrationWindow(CalibrationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch { /* 图标加载失败不影响功能 */ }
        vm.Completed += Close;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
