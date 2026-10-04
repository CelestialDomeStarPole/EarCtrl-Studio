using System.Windows;
using EarCtrl.App.Services;
using EarCtrl.App.Views;
using Wpf.Ui.Controls;

namespace EarCtrl.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // 页面工厂交给 NavigationView 内置导航（条目均声明 TargetPageType）
        NavView.SetPageProviderService(new PageProvider());
        // 首屏导航必须等模板加载完成，否则内部 ContentPresenter 为 null
        Loaded += (_, _) => NavView.Navigate(typeof(DashboardPage));
    }
}
