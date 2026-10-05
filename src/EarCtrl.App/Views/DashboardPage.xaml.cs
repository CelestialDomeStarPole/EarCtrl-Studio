using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.StateMachine;
using EarCtrl.Core.Supervision;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 首页设备状态卡（§5.1 映射：GalaxyBudsClient Dashboard 的电量部分）。
/// 降噪操作在独立 AncPage（K11）；多设备点侧边栏切换全局当前设备（K10）。
/// </summary>
public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        DeviceContext.Instance.CurrentChanged += OnContextChanged;
        DeviceContext.Instance.DevicesChanged += OnContextChanged;
        DeviceContext.Instance.StateChanged += OnStateChanged;
        Refresh();
    }

    private void Unsubscribe()
    {
        DeviceContext.Instance.CurrentChanged -= OnContextChanged;
        DeviceContext.Instance.DevicesChanged -= OnContextChanged;
        DeviceContext.Instance.StateChanged -= OnStateChanged;
    }

    private void OnContextChanged() => SafeRefresh();
    private void OnStateChanged(string brand) => SafeRefresh();

    private void SafeRefresh() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var context = DeviceContext.Instance;
        var profile = context.Current;

        NoDeviceCard.Visibility = profile is null ? Visibility.Visible : Visibility.Collapsed;
        DeviceCard.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        if (profile is null)
        {
            return;
        }

        DeviceNameText.Text = profile.Model.Name;
        ModelText.Text = profile.Model.DeviceModel;

        var state = context.GetState(profile.Brand);
        StatusText.Text = StatusDisplay(state);
        ChargingPanel.Visibility = state.Charging == true ? Visibility.Visible : Visibility.Collapsed;

        LeftPct.Text = Pct(state.BatteryLeft);
        RightPct.Text = Pct(state.BatteryRight);
        CasePct.Text = Pct(state.BatteryCase);
        LeftBar.Value = state.BatteryLeft ?? 0;
        RightBar.Value = state.BatteryRight ?? 0;
        CaseBar.Value = state.BatteryCase ?? 0;

        var connected = state.IsControllable;
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
    }

    private static string Pct(int? value) => value is null ? "--%" : $"{value}%";

    private static string StatusDisplay(DeviceRuntimeState state)
    {
        var text = state.LinkStatus switch
        {
            "connected" => Loc.Tr("Status.Connected"),
            "connecting" => Loc.Tr("Status.Connecting"),
            "disconnected" => Loc.Tr("Status.Disconnected"),
            "reconnecting" => Loc.Tr("Status.Reconnecting"),
            "failed" => Loc.Tr("Status.Failed"),
            _ => state.SupervisorState switch
            {
                AgentState.Idle => Loc.Tr("Status.Idle"),
                AgentState.Detected => Loc.Tr("Status.Detected"),
                AgentState.AgentStarting => Loc.Tr("Status.AgentStarting"),
                AgentState.Connecting => Loc.Tr("Status.Connecting"),
                AgentState.Ready => Loc.Tr("Status.Ready"),
                AgentState.Reconnecting => Loc.Tr("Status.Reconnecting"),
                AgentState.Failed => Loc.Tr("Status.Failed"),
                AgentState.Cooldown => Loc.Tr("Status.Cooldown"),
                AgentState.Disabled => Loc.Tr("Status.Disabled"),
                _ => "--",
            },
        };
        return state.LinkError is { } error && state.LinkStatus == "failed"
            ? $"{text}：{error}"
            : text;
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null) return;
        ConnectButton.IsEnabled = false;
        try
        {
            var error = await AgentHost.Instance.ManualConnectAsync(brand);
            if (error is not null)
            {
                StatusText.Text = error;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null) return;
        DisconnectButton.IsEnabled = false;
        try
        {
            await AgentHost.Instance.ManualDisconnectAsync(brand);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnAddDeviceClick(object sender, RoutedEventArgs e)
    {
        var window = new AddDeviceWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}
