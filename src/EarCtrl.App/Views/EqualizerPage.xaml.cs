using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 均衡器页（M4）：内置预设 + 自定义音效（10 段 -6..+6，2B49 写入并保存）。
/// 交互与其它页一致：纯状态驱动、pending 虚化、未连接虚化、不支持隐藏。
/// 打开页面/缺值时主动读一次 EQ（2B70 → 2B4A），避免会话初始化的读被静默期丢弃后不显示当前模式。
/// </summary>
public partial class EqualizerPage : Page
{
    private sealed record Option(int Value, string Label);

    private const int PresetPendingTimeoutMs = 8000;

    private static readonly string[] BandLabels = ["60", "125", "250", "500", "1K", "2K", "4K", "8K", "12K", "16K"];

    private readonly Dictionary<string, Wpf.Ui.Controls.Button> _buttons = new();
    private readonly List<Slider> _bandSliders = [];
    private string _presetSignature = "";
    private string? _pendingPreset;
    private int _pendingGeneration;
    private string? _hintMessage;

    private string _customSignature = "";
    private bool _loadingCustom;
    private bool _pendingCustom;
    private int? _customPendingTargetId;
    private int _customGeneration;

    private string? _eqQueryBrand;
    private DateTime _eqQueryRetryAt;

    public EqualizerPage()
    {
        InitializeComponent();
        BuildBandSliders();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void BuildBandSliders()
    {
        for (var i = 0; i < BandLabels.Length; i++)
        {
            var panel = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -6,
                Maximum = 6,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Height = 150,
                Value = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = BandLabels[i],
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
            };
            panel.Children.Add(slider);
            panel.Children.Add(label);
            BandPanel.Children.Add(panel);
            _bandSliders.Add(slider);
        }
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
        _eqQueryBrand = null;
        _eqQueryRetryAt = DateTime.MinValue;
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

        // pending 解除判定（必须先于渲染计算）
        if (_pendingPreset is not null && state?.EqPreset == _pendingPreset)
        {
            _pendingPreset = null;
            _pendingGeneration++;
            _hintMessage = null;
        }
        if (_pendingCustom)
        {
            var applied = state?.EqPresetId;
            if (_customPendingTargetId is { } target
                    ? applied == target
                    : applied is >= EqCustomIds.Base)
            {
                _pendingCustom = false;
                _customPendingTargetId = null;
                _customGeneration++;
                _hintMessage = null;
            }
        }
        var presetPending = _pendingPreset is not null;
        var customPending = _pendingCustom;

        // 页面打开/缺值时主动读一次（会话初始化读可能被耳机查询静默期丢弃）
        if (controllable && profile is not null && state?.EqPresetId is null)
        {
            MaybeQueryEq(profile.Brand);
        }

        var avail = FeatureGate.Evaluate(FeatureModules.EqualizerPresets, profile, controllable);
        var supported = avail != FeatureAvailability.Unavailable;
        PresetCard.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;

        EnsureButtons(profile?.Capabilities.Equalizer.Presets ?? []);
        foreach (var (id, button) in _buttons)
        {
            var selected = state?.EqPreset == id;
            button.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
            button.IsEnabled = avail == FeatureAvailability.Available && controllable && !presetPending && !customPending;
            button.Opacity = !controllable || avail == FeatureAvailability.Unknown || presetPending || customPending
                ? 0.4
                : 1.0;
        }

        // 自定义音效卡片
        var customAvail = FeatureGate.Evaluate(FeatureModules.EqualizerCustom, profile, controllable);
        var customSupported = customAvail != FeatureAvailability.Unavailable;
        CustomCard.Visibility = customSupported ? Visibility.Visible : Visibility.Collapsed;
        CustomCard.IsEnabled = customAvail == FeatureAvailability.Available
                               && controllable && !customPending && !presetPending;
        CustomCard.Opacity = !controllable || customAvail == FeatureAvailability.Unknown
                             || customPending || presetPending
            ? 0.4
            : 1.0;
        EnsureCustomModes(state);

        // 「应用」仅对已保存的自定义音效可用；当前生效的就是它时不再重复下发
        var selectedCustom = SelectedCustomMode(state);
        var canApply = customAvail == FeatureAvailability.Available && controllable
                       && !customPending && !presetPending
                       && selectedCustom is not null && state?.EqPresetId != selectedCustom.Id;
        ApplyCustomButton.IsEnabled = canApply;
        ApplyCustomButton.Opacity = canApply ? 1.0 : 0.4;

        if (!supported && !customSupported)
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

    private void MaybeQueryEq(string brand)
    {
        if (_eqQueryBrand == brand && DateTime.UtcNow < _eqQueryRetryAt)
        {
            return;
        }
        _eqQueryBrand = brand;
        _eqQueryRetryAt = DateTime.UtcNow.AddSeconds(30);
        _ = QueryEqAsync(brand);
    }

    private async Task QueryEqAsync(string brand)
    {
        try
        {
            await AgentHost.Instance.QueryEqAsync(brand);
        }
        catch
        {
            // 链路瞬断：30s 后由下一次状态刷新重试
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

        // 进入 pending：所有预设虚化禁改，直到耳机回报目标预设或 8s 超时
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
            await Task.Delay(PresetPendingTimeoutMs);
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

    // ---- 自定义音效 ----

    /// <summary>自定义模式列表/当前生效 id 变化时重建下拉并选中当前模式（仅重建时填充滑块，避免覆盖拖动）。</summary>
    private void EnsureCustomModes(DeviceRuntimeState? state)
    {
        var modes = state?.EqCustomModes;
        var signature = string.Join("|", modes?.Select(m => $"{m.Id}:{m.Name}") ?? [])
                        + "|" + (state?.EqPresetId ?? -1);
        if (signature == _customSignature)
        {
            return;
        }
        _customSignature = signature;

        _loadingCustom = true;
        var items = new List<Option> { new(0, Loc.Tr("Eq.Custom.New")) };
        if (modes is not null)
        {
            items.AddRange(modes.Select(m => new Option(m.Id, m.Name)));
        }
        CustomModeCombo.ItemsSource = items;

        var applied = state?.EqPresetId;
        var current = applied is >= EqCustomIds.Base
            ? modes?.FirstOrDefault(m => m.Id == applied)
            : null;
        CustomModeCombo.SelectedValue = current?.Id ?? 0;
        _loadingCustom = false;
        FillCustomEditor(current);
    }

    private void FillCustomEditor(EqCustomModeState? mode)
    {
        for (var i = 0; i < _bandSliders.Count; i++)
        {
            var value = mode is not null && i < mode.Bands.Length ? mode.Bands[i] : 0;
            _bandSliders[i].Value = Math.Clamp(value, -6, 6);
        }
        CustomNameBox.Text = mode?.Name ?? "";
    }

    /// <summary>当前下拉选中的已保存自定义音效（选「新建」或数据未知时返回 null）。</summary>
    private EqCustomModeState? SelectedCustomMode(DeviceRuntimeState? state)
    {
        if (CustomModeCombo.SelectedValue is not int id || id < EqCustomIds.Base)
        {
            return null;
        }
        return state?.EqCustomModes?.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>
    /// 应用已保存的自定义音效：官方实现（EqAdjustApi.reSendEqAdjust）对 isCustom 模式就是
    /// 重发完整 2B49（param1..5，action=1），与「保存并应用」同一条路径。
    /// </summary>
    private async void OnApplyCustomClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null)
        {
            return;
        }
        var mode = SelectedCustomMode(DeviceContext.Instance.GetState(brand));
        if (mode is null)
        {
            return;
        }

        _hintMessage = null;
        _pendingCustom = true;
        _customPendingTargetId = mode.Id;
        var generation = ++_customGeneration;
        Refresh();
        try
        {
            await AgentHost.Instance.SaveEqCustomAsync(brand, mode.Name, mode.Bands, mode.Id);
        }
        catch (Exception ex)
        {
            _pendingCustom = false;
            _customPendingTargetId = null;
            _customGeneration++;
            _hintMessage = ex.Message;
            Refresh();
            return;
        }
        _ = CustomWatchdogAsync(generation, brand);
    }

    private void OnCustomModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCustom)
        {
            return;
        }
        var id = CustomModeCombo.SelectedValue is int value ? value : 0;
        var modes = DeviceContext.Instance.Current is { } profile
            ? DeviceContext.Instance.GetState(profile.Brand).EqCustomModes
            : null;
        FillCustomEditor(id >= EqCustomIds.Base ? modes?.FirstOrDefault(m => m.Id == id) : null);
        // 选中项变化会影响「应用」可用性（EnsureCustomModes 的签名不含选中项，不会自行刷新）
        Refresh();
    }

    private void OnResetCustomClick(object sender, RoutedEventArgs e)
    {
        foreach (var slider in _bandSliders)
        {
            slider.Value = 0;
        }
    }

    private async void OnSaveCustomClick(object sender, RoutedEventArgs e)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null)
        {
            return;
        }
        var bands = _bandSliders.Select(s => (int)Math.Round(s.Value)).ToArray();
        var name = CustomNameBox.Text.Trim();
        var id = CustomModeCombo.SelectedValue is int value && value >= EqCustomIds.Base
            ? value
            : (int?)null;

        _hintMessage = null;
        _pendingCustom = true;
        _customPendingTargetId = id;
        var generation = ++_customGeneration;
        Refresh();
        try
        {
            await AgentHost.Instance.SaveEqCustomAsync(brand, name, bands, id);
        }
        catch (Exception ex)
        {
            _pendingCustom = false;
            _customPendingTargetId = null;
            _customGeneration++;
            _hintMessage = ex.Message;
            Refresh();
            return;
        }
        _ = CustomWatchdogAsync(generation, brand);
    }

    private async Task CustomWatchdogAsync(int generation, string brand)
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(2000);
            if (generation != _customGeneration || !_pendingCustom)
            {
                return;
            }
            try
            {
                await AgentHost.Instance.QueryEqAsync(brand);
            }
            catch
            {
                // 链路瞬断：继续轮询，最终超时提示
            }
        }

        if (generation != _customGeneration || !_pendingCustom)
        {
            return;
        }
        _pendingCustom = false;
        _customPendingTargetId = null;
        _customGeneration++;
        _hintMessage = Loc.Tr("Eq.Custom.Timeout");
        Refresh();
    }
}
