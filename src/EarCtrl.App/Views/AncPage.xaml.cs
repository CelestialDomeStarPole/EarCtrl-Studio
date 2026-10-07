using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 降噪页（K11）：模式三选（降噪/透传/关闭）+ 降噪等级下拉（仅降噪模式可用），
/// 作用于 K10 全局当前设备。动态等级选项按 Profile 能力（ancDynamicLevel，
/// 对照 OpenFreebuds per_model 的 w_cancel_dynamic 名单）显示。
/// </summary>
public partial class AncPage : Page
{
    private bool _suppressLevelChanged;

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
        var controllable = state is { IsControllable: true };
        var mode = state?.AncModeRaw;

        // 模式三按钮选中态（华为原始字节：0=关闭 1=降噪 2=透传）——跟随耳机确认的真状态
        foreach (var button in new Wpf.Ui.Controls.Button[] { BtnCancellation, BtnAwareness, BtnOff })
        {
            var selected = controllable && (string)button.Tag == ModeTag(mode);
            button.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
            button.IsEnabled = controllable;
        }

        // 等级行常驻：仅「降噪」模式可选（未选中时虚化禁用）；动态项按 Profile 能力显示
        var levelEnabled = controllable && mode == 1;
        _suppressLevelChanged = true;
        var options = new List<KeyValuePair<string, string>>
        {
            new("normal", Loc.Tr("Anc.Level.Normal")),
            new("comfort", Loc.Tr("Anc.Level.Comfort")),
            new("ultra", Loc.Tr("Anc.Level.Ultra")),
        };
        if (profile?.Capabilities.AncDynamicLevel == true)
        {
            options.Add(new("dynamic", Loc.Tr("Anc.Level.Dynamic")));
        }
        LevelCombo.ItemsSource = options;
        LevelCombo.SelectedValue = LevelTag(state?.AncLevelRaw);
        _suppressLevelChanged = false;
        LevelPanel.Opacity = levelEnabled ? 1.0 : 0.4;
        LevelPanel.IsHitTestVisible = levelEnabled;
        LevelCombo.IsEnabled = levelEnabled;

        HintText.Visibility = controllable ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string? ModeTag(int? mode) => mode switch
    {
        1 => "cancellation",
        2 => "awareness",
        0 => "normal",
        _ => null,
    };

    private static string? LevelTag(int? level) => level switch
    {
        0 => "normal",
        1 => "comfort",
        2 => "ultra",
        3 => "dynamic",
        _ => null,
    };

    private async void OnModeClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null || sender is not Wpf.Ui.Controls.Button { Tag: string mode })
        {
            return;
        }

        try
        {
            await AgentHost.Instance.SetAncModeAsync(brand, mode);
        }
        catch (Exception ex)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = ex.Message;
        }
        Refresh();
    }

    private async void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLevelChanged || LevelCombo.SelectedValue is not string level)
        {
            return;
        }
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null)
        {
            return;
        }

        try
        {
            await AgentHost.Instance.SetAncModeAsync(brand, "cancellation", level);
        }
        catch (Exception ex)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = ex.Message;
        }
    }
}
