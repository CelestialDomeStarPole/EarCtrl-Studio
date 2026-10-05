using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EarCtrl.App.Services;
using EarCtrl.App.Views;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App;

public partial class MainWindow : FluentWindow
{
    /// 设备子项引用（绿条可见性更新用）。
    private readonly List<(System.Windows.Shapes.Rectangle Green, string Brand)> _deviceItems = [];
    /// 当前显示的是否为「我的耳机」（Dashboard）界面 → 蓝条（内置选中态）。
    private bool _dashboardVisible;

    public MainWindow()
    {
        InitializeComponent();
        // 页面工厂交给 NavigationView 内置导航（条目均声明 TargetPageType）
        NavView.SetPageProviderService(new PageProvider());
        // 「我的耳机」父项点击走 Click 事件（收起状态点击 = 展开侧边栏 + 设备文件夹）
        MyDevicesItem.Click += OnMyDevicesItemClick;
        PageProvider.PageDisplayed += OnPageDisplayed;
        // 设备列表/当前设备变化 → 重建侧边栏设备区
        DeviceContext.Instance.DevicesChanged += OnContextChanged;
        DeviceContext.Instance.CurrentChanged += OnContextChanged;
        // 首屏导航必须等模板加载完成，否则内部 ContentPresenter 为 null
        Loaded += (_, _) =>
        {
            BuildDeviceItems();
            NavView.Navigate(typeof(DashboardPage));
        };
        // 语言切换后设备子项文案需重建
        Loc.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(BuildDeviceItems);

    private void OnContextChanged() => Dispatcher.BeginInvoke(BuildDeviceItems);

    private void OnPageDisplayed(Type pageType)
    {
        var isDashboard = pageType == typeof(DashboardPage);
        if (_dashboardVisible == isDashboard) return;
        _dashboardVisible = isDashboard;
        Dispatcher.BeginInvoke(UpdateIndicators);
    }

    // 更新指示：蓝条（内置选中态）= 正在显示「我的耳机」界面；绿条 = 当前选中设备
    private void UpdateIndicators()
    {
        MyDevicesItem.IsActive = _dashboardVisible;
        var current = DeviceContext.Instance.Current?.Brand;
        foreach (var (green, brand) in _deviceItems)
        {
            green.Visibility = brand == current ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // 「我的耳机」父项点击：侧边栏收起时展开侧边栏 + 设备文件夹（不导航）
    private void OnMyDevicesItemClick(object sender, RoutedEventArgs e)
    {
        if (!NavView.IsPaneOpen)
        {
            NavView.IsPaneOpen = true;
            MyDevicesItem.IsExpanded = true;
        }
    }

    // 设备子项点击：设为全局当前设备（K10，其它页面自动跟随）+ 打开「我的耳机」界面
    private void OnDeviceItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationViewItem { Tag: string tag } &&
            tag.StartsWith("device:", StringComparison.Ordinal))
        {
            DeviceContext.Instance.SetCurrent(tag["device:".Length..]);
            NavView.Navigate(typeof(DashboardPage));
        }
    }

    // 重建「我的耳机」集合：每个已添加设备一个子项（添加入口在设备主页右上角）。
    // 子项 = [绿条=当前选中设备][喇叭][设备名]，零人为间隙；蓝条用内置选中态。
    private void BuildDeviceItems()
    {
        MyDevicesItem.MenuItems.Clear();
        _deviceItems.Clear();

        var currentBrand = DeviceContext.Instance.Current?.Brand;
        foreach (var device in DeviceContext.Instance.Devices)
        {
            var isCurrent = device.Brand == currentBrand;
            var green = MakeIndicator(GreenBrush, isCurrent);
            green.Margin = new Thickness(0, 0, 8, 0); // 绿条与喇叭的间距
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(green);
            panel.Children.Add(new SymbolIcon(SymbolRegular.SpeakerBluetooth24) { FontSize = 18 });
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = device.Model.Name,
                VerticalAlignment = VerticalAlignment.Center,
                // 用 Padding 而非 Margin 设间距，避免被 ReduceIndent 当作缩进收缩
                Padding = new Thickness(10, 0, 0, 0),
            });

            var item = new NavigationViewItem
            {
                Tag = $"device:{device.Brand}",
                Content = panel,
                IsActive = _dashboardVisible && isCurrent,
                Padding = new Thickness(2, 0, 2, 0),
            };
            item.Loaded += (s, _) => ReduceIndent((NavigationViewItem)s!);
            item.Click += OnDeviceItemClick;
            _deviceItems.Add((green, device.Brand));
            MyDevicesItem.MenuItems.Add(item);
        }
    }

    // 收缩 WPF-UI 模板的子项深度缩进：遍历视觉树，把超过 2px 的左 margin 压到 2px。
    // 缩进由模板内部元素的固定 margin 产生（不在条目自身 Padding/Margin 上），只能模板级收缩。
    private static void ReduceIndent(NavigationViewItem item)
    {
        void Walk(DependencyObject node)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe && fe.Margin.Left > 2)
                {
                    fe.Margin = new Thickness(2, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
                }
                Walk(child);
            }
        }
        Walk(item);
    }

    private static System.Windows.Shapes.Rectangle MakeIndicator(System.Windows.Media.Brush brush, bool visible) =>
        new()
        {
            Width = 3,
            Height = 18,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
        };

    private static readonly System.Windows.Media.Brush GreenBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71));

    // 关窗 = 最小化到托盘；真正退出走托盘菜单（App.ForceShutdown）
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
