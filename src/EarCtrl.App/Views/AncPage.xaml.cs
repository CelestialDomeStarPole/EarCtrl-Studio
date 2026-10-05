using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 降噪五态页（K11，M3）：大号分段切换，作用于 K10 全局当前设备。
/// 展示规则（协议笔记 5.1-2）：mode=0 显示「关闭」，档位仅作缓存。
/// </summary>
public partial class AncPage : Page
{
    public AncPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        DeviceContext.Instance.CurrentChanged += OnContextChanged;
        DeviceContext.Instance.StateChanged += OnStateChanged;
        Refresh();
    }

    private void Unsubscribe()
    {
        DeviceContext.Instance.CurrentChanged -= OnContextChanged;
        DeviceContext.Instance.StateChanged -= OnStateChanged;
    }

    private void OnContextChanged() => SafeRefresh();
    private void OnStateChanged(string brand) => SafeRefresh();

    private void SafeRefresh() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var context = DeviceContext.Instance;
        var profile = context.Current;
        CurrentDeviceRun.Text = profile?.Model.Name ?? "--";

        var state = profile is null ? null : context.GetState(profile.Brand);
        var current = state?.Anc ?? AncFiveState.Unknown;
        var controllable = state is { IsControllable: true };

        foreach (var button in new Wpf.Ui.Controls.Button[] { BtnUltra, BtnGeneral, BtnCozy, BtnTransparency, BtnOff })
        {
            var selected = controllable &&
                           Enum.TryParse<AncFiveState>((string)button.Tag, out var parsed) &&
                           parsed == current;
            button.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
            button.IsEnabled = controllable;
        }

        HintText.Visibility = controllable ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnStateClick(object sender, RoutedEventArgs e)
    {
        var context = DeviceContext.Instance;
        var brand = context.Current?.Brand;
        if (brand is null || sender is not Wpf.Ui.Controls.Button { Tag: string tag } ||
            !Enum.TryParse<AncFiveState>(tag, out var state))
        {
            return;
        }

        try
        {
            await AgentHost.Instance.SetAncModeAsync(brand, state);
        }
        catch (Exception ex)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = ex.Message;
        }
        Refresh();
    }
}
