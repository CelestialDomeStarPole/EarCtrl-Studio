using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.Config;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 系统集成页（M4）：低电量托盘提醒（阈值可调）、每设备 Agent 手动开关。
/// Agent 开关为黑名单语义（关闭 = 杀进程且不再自动拉起），持久化到 config.DisabledAgents。
/// </summary>
public partial class IntegrationPage : Page
{
    private static readonly IReadOnlyList<KeyValuePair<int, string>> ThresholdOptions =
    [
        new(10, "10%"),
        new(20, "20%"),
        new(30, "30%"),
    ];

    /// <summary>初始化期间抑制控件事件（避免把读取配置当成用户修改）。</summary>
    private bool _loading;

    public IntegrationPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        _loading = true;
        LowBatteryToggle.IsChecked = App.Config.LowBatteryNotify;
        ThresholdCombo.ItemsSource = ThresholdOptions;
        ThresholdCombo.SelectedValue = App.Config.LowBatteryThreshold;
        _loading = false;

        BuildAgentRows();
        DeviceContext.Instance.DevicesChanged += OnDevicesChanged;
    }

    private void Unsubscribe()
    {
        DeviceContext.Instance.DevicesChanged -= OnDevicesChanged;
    }

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(BuildAgentRows);

    private void OnLowBatteryToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        App.Config.LowBatteryNotify = LowBatteryToggle.IsChecked == true;
        ConfigStore.Save(App.Config);
    }

    private void OnThresholdChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThresholdCombo.SelectedValue is not int value)
        {
            return;
        }
        App.Config.LowBatteryThreshold = value;
        ConfigStore.Save(App.Config);
    }

    /// <summary>每设备一行：设备名 + Agent 开关（当前状态来自 supervisor.Enabled）。</summary>
    private void BuildAgentRows()
    {
        AgentPanel.Children.Clear();
        var devices = DeviceContext.Instance.Devices;
        NoAgentText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var device in devices)
        {
            var supervisor = AgentHost.Instance.GetSupervisor(device.Brand);

            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new System.Windows.Controls.TextBlock
            {
                Text = device.Model.Name,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var toggle = new ToggleSwitch
            {
                IsChecked = supervisor?.Enabled ?? true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Click += (_, _) => AgentHost.Instance.SetAgentEnabled(device.Brand, toggle.IsChecked == true);

            Grid.SetColumn(label, 0);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(label);
            row.Children.Add(toggle);
            AgentPanel.Children.Add(row);
        }
    }
}
