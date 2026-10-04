using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EarCtrl.App.Services;

namespace EarCtrl.App.Views;

/// <summary>
/// 占位页基类（纯代码构建，无 XAML）：M0 仅展示标题与「后续里程碑实现」提示。
/// 每个导航项的具体占位类型见 Placeholders.cs（WPF-UI 导航需要唯一 TargetPageType）。
/// </summary>
public class UnderConstructionPage : Page
{
    public UnderConstructionPage(string titleKey)
    {
        var title = new Wpf.Ui.Controls.TextBlock
        {
            FontTypography = Wpf.Ui.Controls.FontTypography.Subtitle,
            Text = Loc.Tr(titleKey),
        };
        var hint = new Wpf.Ui.Controls.TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Text = Loc.Tr("Page.UnderConstruction"),
            Opacity = 0.7,
        };

        var card = new Wpf.Ui.Controls.Card
        {
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Children = { title, hint } },
        };

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(28, 20, 28, 28),
                Children = { card },
            },
        };
        Background = Brushes.Transparent;
    }
}
