using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 均衡器页（M4）：按 Profile 能力（equalizer.presets）显示内置预设，点击切换。
/// 交互与降噪页一致：纯状态驱动（勾选跟随耳机 2B4A 回报）、点击后 pending 虚化（8s 上限）、
/// 未连接显示虚化、设备不支持则隐藏（能力门 §5.5）。
/// </summary>
public partial class EqualizerPage : Page
{
    private readonly Dictionary<string, Wpf.Ui.Controls.Button> _buttons = new();
    private string _presetSignature = "";
    private string? _pendingPreset;
    private int _pendingGeneration;
    private const int PendingTimeoutMs = 8000;
    private string? _hintMessage;

    public EqualizerPage()
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

        // pending 解除判定（必须先于按钮渲染计算）
        if (_pendingPreset is not null && state?.EqPreset == _pendingPreset)
        {
            _pendingPreset = null;
            _pendingGeneration++;
            _hintMessage = null;
        }
        var pending = _pendingPreset is not null;

        var avail = FeatureGate.Evaluate(FeatureModules.EqualizerPresets, profile, controllable);
        var supported = avail != FeatureAvailability.Unavailable;
        PresetCard.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;

        EnsureButtons(profile?.Capabilities.Equalizer.Presets ?? []);
        foreach (var (id, button) in _buttons)
        {
            var selected = state?.EqPreset == id;
            button.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
            button.IsEnabled = avail == FeatureAvailability.Available && controllable && !pending;
            button.Opacity = !controllable || avail == FeatureAvailability.Unknown || pending ? 0.4 : 1.0;
        }

        if (!supported)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = Loc.Tr("Eq.NotSupported");
        }
        else if (!controllable)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = Loc.Tr("Anc.NotReady");
        }
        else if (_hintMessage is { } message)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = message;
        }
        else
        {
            HintText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>预设按钮只在列表变化时重建（避免每次状态刷新重建控件）。</summary>
    private void EnsureButtons(IReadOnlyList<string> presets)
    {
        var signature = string.Join('|', presets);
        if (signature == _presetSignature)
        {
            return;
        }
        _presetSignature = signature;
        _buttons.Clear();
        PresetPanel.Children.Clear();
        foreach (var preset in presets)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = PresetLabel(preset),
                Tag = preset,
                Appearance = ControlAppearance.Secondary,
                MinWidth = 150,
                Height = 44,
                Margin = new Thickness(0, 0, 8, 8),
            };
            button.Click += OnPresetClick;
            _buttons[preset] = button;
            PresetPanel.Children.Add(button);
        }
    }

    private static string PresetLabel(string preset) => preset switch
    {
        "default" => Loc.Tr("Eq.Preset.default"),
        "hardbass" => Loc.Tr("Eq.Preset.hardbass"),
        "treble" => Loc.Tr("Eq.Preset.treble"),
        "voices" => Loc.Tr("Eq.Preset.voices"),
        _ => preset,
    };

    private async void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { Tag: string preset })
        {
            return;
        }
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null)
        {
            return;
        }

        // 进入 pending：所有预设虚化禁改，直到耳机回报目标预设（Refresh 检测）或 8s 超时
        _hintMessage = null;
        _pendingPreset = preset;
        var generation = ++_pendingGeneration;
        Refresh();
        try
        {
            await AgentHost.Instance.SetEqAsync(brand, preset);
            PendingWatchdogAsync(generation); // async void 即发即忘
        }
        catch (Exception ex)
        {
            _pendingPreset = null;
            _pendingGeneration++;
            _hintMessage = ex.Message;
            Refresh();
        }
    }

    private async void PendingWatchdogAsync(int generation)
    {
        try
        {
            await Task.Delay(PendingTimeoutMs);
        }
        catch
        {
            return;
        }
        if (generation != _pendingGeneration || _pendingPreset is null)
        {
            return; // 已被回报/新请求解除
        }
        _pendingPreset = null;
        _pendingGeneration++;
        _hintMessage = Loc.Tr("Anc.SetTimeout");
        Refresh();
    }
}
