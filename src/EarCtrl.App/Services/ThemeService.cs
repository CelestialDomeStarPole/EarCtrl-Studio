using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Services;

/// <summary>
/// 主题服务（K6）：深/浅/跟随系统。
/// 实现说明：统一使用 <see cref="WindowBackdropType.None"/>（纯色窗口背景）。
/// 原因：WPF-UI 的 Apply 默认叠加 Mica 背景效果，深→浅切换时窗口 Mica 背景不会重刷，
/// 导致侧边栏/标题栏区域残留黑色（M0 已实测）。M4 打磨期再评估 Mica/Acrylic 与运行时跟随系统。
/// </summary>
public static class ThemeService
{
    public const string FollowSystem = "FollowSystem";
    public const string Dark = "Dark";
    public const string Light = "Light";

    public static void Apply(string mode)
    {
        var theme = mode switch
        {
            Dark => ApplicationTheme.Dark,
            Light => ApplicationTheme.Light,
            _ => IsSystemDark() ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None);
    }

    /// <summary>读取系统「应用模式」设置（AppsUseLightTheme），默认深色。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }
}
