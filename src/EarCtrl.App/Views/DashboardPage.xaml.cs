using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.App.Views;
using EarCtrl.Core.StateMachine;
using EarCtrl.Core.Supervision;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 设备主页（对应当前设备，K10）：顶部横向设备切换栏 + GalaxyBudsClient 风格状态卡片。
/// 入耳式 = 左右电量单元（电量/电压/温度/佩戴状态）+ 中央耳机图 + 充电仓电量；
/// 头戴式 = 单电量单元 + 头戴图；连接/断开在卡片右下角。其它页面跟随当前设备。
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
        RefreshAll();
    }

    private void Unsubscribe()
    {
        DeviceContext.Instance.CurrentChanged -= OnContextChanged;
        DeviceContext.Instance.DevicesChanged -= OnContextChanged;
        DeviceContext.Instance.StateChanged -= OnStateChanged;
    }

    private void OnContextChanged() => SafeRefresh();
    private void OnStateChanged(string brand) => SafeRefresh();

    private void SafeRefresh() => Dispatcher.BeginInvoke(RefreshAll);

    private void RefreshAll()
    {
        BuildDeviceBar();
        RefreshCard();
    }

    // ---- 设备切换栏 ----

    private void BuildDeviceBar()
    {
        DeviceBar.Children.Clear();
        var current = DeviceContext.Instance.Current?.Brand;
        foreach (var device in DeviceContext.Instance.Devices)
        {
            var isCurrent = device.Brand == current;
            var chip = new Wpf.Ui.Controls.Button
            {
                Content = device.Model.Name,
                Appearance = isCurrent ? ControlAppearance.Primary : ControlAppearance.Secondary,
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 110,
            };
            var brand = device.Brand;
            chip.Click += (_, _) => DeviceContext.Instance.SetCurrent(brand);
            DeviceBar.Children.Add(chip);
        }
    }

    // ---- 设备卡片 ----

    private void RefreshCard()
    {
        var context = DeviceContext.Instance;
        var profile = context.Current;

        NoDeviceCard.Visibility = profile is null ? Visibility.Visible : Visibility.Collapsed;
        EarbudsCard.Visibility = profile is { FormFactor: not "overhead" } ? Visibility.Visible : Visibility.Collapsed;
        HeadsetCard.Visibility = profile is { FormFactor: "overhead" } ? Visibility.Visible : Visibility.Collapsed;
        if (profile is null)
        {
            return;
        }

        var state = context.GetState(profile.Brand);
        var isOverhead = profile.FormFactor == "overhead";
        RefreshCardButtons(isOverhead, state.IsControllable);

        if (isOverhead)
        {
            // 头戴式：单电量单元（电量/电压/温度）
            HeadPct.Text = Pct(state.BatteryLeft);
            HeadVoltage.Text = Voltage(state.VoltageLeft);
            HeadTemp.Text = Temp(state.TempLeft);
            return;
        }

        // 入耳式电量显示规则：关盖在盒 → --%（协议 0 占位）；开盖在盒且电量为 0 → --%（切换帧占位，不可信）；
        // 开盖在盒电量 >0 → 数值；佩戴态忠实显示（0% 为真实电量）
        var leftHideBatt = state.InCaseLeft == true && (state.LidClosedLeft == true || state.BatteryLeft == 0);
        var rightHideBatt = state.InCaseRight == true && (state.LidClosedRight == true || state.BatteryRight == 0);
        var leftLid = state.LidClosedLeft == true;
        var rightLid = state.LidClosedRight == true;
        LeftPct.Text = leftHideBatt ? "--%" : Pct(state.BatteryLeft);
        LeftVoltage.Text = Voltage(state.VoltageLeft);
        LeftTemp.Text = Temp(state.TempLeft);
        LeftStatus.Text = WearState(state.InCaseLeft, leftLid);

        RightPct.Text = rightHideBatt ? "--%" : Pct(state.BatteryRight);
        RightVoltage.Text = Voltage(state.VoltageRight);
        RightTemp.Text = Temp(state.TempRight);
        RightStatus.Text = WearState(state.InCaseRight, rightLid);

        // 充电仓行：profile 声明支持才显示；充电中标识按 param3 第三字节（CaseCharging）
        var hasCase = profile.Capabilities.Battery.Case;
        CaseIcon.Visibility = hasCase ? Visibility.Visible : Visibility.Collapsed;
        CaseText.Visibility = hasCase ? Visibility.Visible : Visibility.Collapsed;
        CaseText.Text = $"{Loc.Tr("Battery.Case")} {Pct(state.BatteryCase)}";
        CaseChargingText.Visibility = hasCase && state.CaseCharging == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshCardButtons(bool isOverhead, bool controllable)
    {
        var connect = isOverhead ? (Wpf.Ui.Controls.Button?)HeadsetConnect : EarbudsConnect;
        var disconnect = isOverhead ? (Wpf.Ui.Controls.Button?)HeadsetDisconnect : EarbudsDisconnect;
        connect.IsEnabled = !controllable;
        disconnect.IsEnabled = controllable;
    }

    private static string Pct(int? value) => value is null ? "--%" : $"{value}%";

    private static string Voltage(double? value) => value is null ? "--" : $"{value:0.00}V";

    private static string Temp(double? value) => value is null ? "--" : $"{value:0.0}°C";

    private static string WearState(bool? inCase, bool lidClosed) => inCase switch
    {
        null => "--",
        true => Loc.Tr(lidClosed ? "Battery.InCaseClosed" : "Battery.InCaseOpen"),
        false => Loc.Tr("Battery.Wearing"),
    };

    // ---- 按钮 ----

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null) return;
        try
        {
            var error = await AgentHost.Instance.ManualConnectAsync(brand);
            if (error is not null)
            {
                System.Windows.MessageBox.Show(error, "EarCtrl Studio",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "EarCtrl Studio",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null) return;
        try
        {
            await AgentHost.Instance.ManualDisconnectAsync(brand);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "EarCtrl Studio",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OnAddDeviceClick(object sender, RoutedEventArgs e)
    {
        new AddDeviceWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
