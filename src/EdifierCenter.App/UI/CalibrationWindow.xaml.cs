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
        vm.Completed += Close;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
