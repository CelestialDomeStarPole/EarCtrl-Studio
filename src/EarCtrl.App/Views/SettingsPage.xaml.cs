using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;

namespace EarCtrl.App.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        // 每次进入设置页都同步注册表真值（用户可能从外部改过启动项）
        Loaded += (_, _) => AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (!AutoStartService.SetEnabled(AutoStartToggle.IsChecked == true))
        {
            System.Windows.MessageBox.Show(Loc.Tr("Settings.AutoStart.Failed"), "EarCtrl Studio",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
        }
    }
}
