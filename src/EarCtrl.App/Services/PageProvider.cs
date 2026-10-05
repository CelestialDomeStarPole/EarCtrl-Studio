using EarCtrl.App.Views;
using Wpf.Ui.Abstractions;

namespace EarCtrl.App.Services;

/// <summary>
/// WPF-UI NavigationView 页面工厂：按 TargetPageType 提供页面实例。
/// 详见《软件实现计划》§5.1 页面映射表。
/// </summary>
public sealed class PageProvider : INavigationViewPageProvider
{
    /// <summary>导航到的页面类型变化（MainWindow 据此更新设备子项蓝条 = 当前显示的是设备主页）。</summary>
    public static event Action<Type>? PageDisplayed;

    public object? GetPage(Type pageType)
    {
        PageDisplayed?.Invoke(pageType);
        return pageType switch
    {
        var t when t == typeof(DashboardPage) => new DashboardPage(),
        var t when t == typeof(SettingsPage) => new SettingsPage(),
        var t when t == typeof(AncPage) => new AncPage(),
        var t when t == typeof(EqualizerPlaceholder) => new EqualizerPlaceholder(),
        var t when t == typeof(ControlsPlaceholder) => new ControlsPlaceholder(),
        var t when t == typeof(IntegrationPlaceholder) => new IntegrationPlaceholder(),
        var t when t == typeof(AdvancedPlaceholder) => new AdvancedPlaceholder(),
        var t when t == typeof(DevToolsPlaceholder) => new DevToolsPlaceholder(),
        _ => null,
    };
    }
}
