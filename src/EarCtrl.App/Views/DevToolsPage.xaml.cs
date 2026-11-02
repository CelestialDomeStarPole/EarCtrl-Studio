using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 调试台（M4）：订阅 Agent 原始帧（IPC debugSubscribeRaw → rawPacket 事件）+ 发送原始帧（debugSendRaw）。
/// 入口默认隐藏，设置页「开发者选项」开启后出现在侧边栏（UiState.DevToolsVisible）。
/// 页面打开即订阅当前设备，切换设备/离开页面自动改订阅；日志上限 400 行。
/// </summary>
public partial class DevToolsPage : Page
{
    private readonly Queue<string> _lines = new();
    private const int MaxLines = 400;
    private string? _subscribedBrand;
    private bool _wantSubscribe = true;

    public DevToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        AgentHost.Instance.RawPacketReceived += OnRawPacket;
        DeviceContext.Instance.CurrentChanged += OnContextChanged;
        DeviceContext.Instance.StateChanged += OnStateChanged;
        _subscribedBrand = DeviceContext.Instance.Current?.Brand;
        _ = ApplySubscriptionAsync();
        Refresh();
    }

    private void Unsubscribe()
    {
        AgentHost.Instance.RawPacketReceived -= OnRawPacket;
        DeviceContext.Instance.CurrentChanged -= OnContextChanged;
        DeviceContext.Instance.StateChanged -= OnStateChanged;
        var brand = _subscribedBrand;
        _subscribedBrand = null;
        if (brand is not null)
        {
            _ = SafeSetSubscriptionAsync(brand, false);
        }
    }

    private void OnContextChanged() => Dispatcher.BeginInvoke(() =>
    {
        _ = ApplySubscriptionAsync();
        Refresh();
    });

    private void OnStateChanged(string brand) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var profile = DeviceContext.Instance.Current;
        CurrentDeviceRun.Text = profile?.Model.Name ?? "--";
        var state = profile is null ? null : DeviceContext.Instance.GetState(profile.Brand);
        SendButton.IsEnabled = state is { IsControllable: true };

        if (state is not { IsControllable: true })
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = Loc.Tr("Anc.NotReady");
        }
        else
        {
            HintText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>订阅状态与当前设备对齐：切设备先退订旧设备，再订阅新设备（仅页面打开期间）。</summary>
    private async Task ApplySubscriptionAsync()
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (_subscribedBrand == brand)
        {
            if (brand is not null && _wantSubscribe)
            {
                await SafeSetSubscriptionAsync(brand, true);
            }
            return;
        }

        var previous = _subscribedBrand;
        _subscribedBrand = brand;
        if (previous is not null)
        {
            await SafeSetSubscriptionAsync(previous, false);
        }
        if (brand is not null && _wantSubscribe)
        {
            await SafeSetSubscriptionAsync(brand, true);
        }
    }

    private static async Task SafeSetSubscriptionAsync(string brand, bool subscribe)
    {
        try
        {
            await AgentHost.Instance.SetRawSubscriptionAsync(brand, subscribe);
        }
        catch
        {
            // Agent 未就绪/链路断开：忽略（设备就绪后再次进入页面会重新订阅）
        }
    }

    private async void OnSubscribeToggled(object sender, RoutedEventArgs e)
    {
        // ToggleSwitch 无 Toggled：Click 只在用户点击时触发，程序化设置 IsChecked 不会回环
        _wantSubscribe = SubscribeToggle.IsChecked == true;
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null)
        {
            Refresh();
            return;
        }
        await SafeSetSubscriptionAsync(brand, _wantSubscribe);
        Refresh();
    }

    private void OnRawPacket(string brand, string dir, string hex)
    {
        if (brand != DeviceContext.Instance.Current?.Brand)
        {
            return; // 只显示当前设备的帧（切换设备后旧帧不串台）
        }
        Dispatcher.BeginInvoke(() =>
            Append($"{DateTime.Now:HH:mm:ss.fff} {dir}  {hex}"));
    }

    private void Append(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > MaxLines)
        {
            _lines.Dequeue();
        }
        LogBox.Text = string.Join(Environment.NewLine, _lines);
        LogBox.ScrollToEnd();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _lines.Clear();
        LogBox.Clear();
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        var hex = RawHexBox.Text.Trim().Replace(" ", "").Replace("-", "");
        if (brand is null || hex.Length == 0)
        {
            return;
        }
        try
        {
            await AgentHost.Instance.SendRawAsync(brand, hex);
            Append($"{DateTime.Now:HH:mm:ss.fff} >>  {hex}");
            HintText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = ex.Message;
        }
    }
}
