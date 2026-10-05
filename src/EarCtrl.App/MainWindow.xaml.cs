using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.App.Views;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // 页面工厂交给 NavigationView 内置导航（条目均声明 TargetPageType）
        NavView.SetPageProviderService(new PageProvider());
        // 侧边栏设备区（K10）：点选 = 设定全局当前设备；无 TargetPageType，不触发页面导航
        NavView.ItemInvoked += OnItemInvoked;
        // 设备列表/当前设备变化 → 重建侧边栏设备区
        DeviceContext.Instance.DevicesChanged += OnContextChanged;
        DeviceContext.Instance.CurrentChanged += OnContextChanged;
        // 首屏导航必须等模板加载完成，否则内部 ContentPresenter 为 null
        Loaded += (_, _) =>
        {
            BuildDeviceItems();
            NavView.Navigate(typeof(DashboardPage));
        };
        // 语言切换后设备区标题需重建
        Loc.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(BuildDeviceItems);

    private void OnContextChanged() => Dispatcher.BeginInvoke(BuildDeviceItems);

    private void OnItemInvoked(NavigationView sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is NavigationViewItem { Tag: string tag } &&
            tag.StartsWith("device:", StringComparison.Ordinal))
        {
            DeviceContext.Instance.SetCurrent(tag["device:".Length..]);
        }
    }

    /// <summary>重建侧边栏设备区：插入在菜单最上方（设备头 + 每个已添加设备一项）。</summary>
    private void BuildDeviceItems()
    {
        // 移除旧设备条目（Tag 形如 device:<brand>）
        for (var i = NavView.MenuItems.Count - 1; i >= 0; i--)
        {
            if (NavView.MenuItems[i] is NavigationViewItem { Tag: string tag } &&
                tag.StartsWith("device:", StringComparison.Ordinal))
            {
                NavView.MenuItems.RemoveAt(i);
            }
        }

        var devices = DeviceContext.Instance.Devices;
        if (devices.Count == 0) return;

        var currentBrand = DeviceContext.Instance.Current?.Brand;
        NavView.MenuItems.Insert(0, new NavigationViewItemHeader
        {
            Text = Loc.Tr("Nav.Devices"),
        });
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            NavView.MenuItems.Insert(1 + i, new NavigationViewItem
            {
                Tag = $"device:{device.Brand}",
                Icon = new SymbolIcon(SymbolRegular.SurfaceEarbuds24),
                Content = device.Model.Name,
                IsActive = device.Brand == currentBrand,
            });
        }
    }

    /// <summary>关窗 = 最小化到托盘；真正退出走托盘菜单（App.ForceShutdown）。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.ForceShutdown)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
