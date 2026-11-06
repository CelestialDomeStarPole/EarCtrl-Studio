using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 触控页（M4）：长按（左/右/通话中）+ 双击 + 三击 + 连击（五下）+ 长按切换降噪循环模式。
/// 协议（真机/官方逆向验证，协议笔记 §7.10）：
///   长按 2B17/2B19 读、2B16/2B18 写；双击 01 20/01 1F；三击 01 26/01 25；连击 2B B4 / 0x14。
/// 交互与其它页一致：状态驱动（无乐观更新）、pending 虚化（10s 上限）、未连接虚化、不支持隐藏。
/// </summary>
public partial class ControlsPage : Page
{
    private sealed record Option(string Value, string Label);

    private sealed record GestureRow(
        ComboBox Combo,
        Grid Row,
        string Gesture,
        string Kind,
        string? Side,
        string ModuleId,
        Func<DeviceRuntimeState, int?> ReadState,
        IReadOnlyDictionary<string, int> Values);

    private static readonly Dictionary<string, int> LongActionRaw = new()
    {
        ["off"] = -1,
        ["anc-cycle"] = 10,
        ["voice-assistant"] = 0,
        ["volume-up"] = 18,
        ["volume-down"] = 19,
    };

    private static readonly Dictionary<string, int> LongInCallRaw = new()
    {
        ["off"] = -1,
        ["reject-call"] = 0,
    };

    private static readonly Dictionary<string, int> DoubleRaw = new()
    {
        ["off"] = -1,
        ["voice-assistant"] = 0,
        ["play-pause"] = 1,
        ["next"] = 2,
        ["prev"] = 7,
    };

    private static readonly Dictionary<string, int> DoubleInCallRaw = new()
    {
        ["off"] = -1,
        ["answer"] = 0,
    };

    private static readonly Dictionary<string, int> TripleRaw = new()
    {
        ["off"] = -1,
        ["next"] = 2,
        ["prev"] = 7,
    };

    private static readonly Dictionary<string, int> MultiRaw = new()
    {
        ["off"] = -1,
        ["volume-up"] = 0,
        ["volume-down"] = 1,
    };

    private static readonly Dictionary<string, int> CycleRaw = new()
    {
        ["1"] = 1,
        ["2"] = 2,
        ["3"] = 3,
        ["4"] = 4,
    };

    private const int PendingTimeoutMs = 10000;

    private readonly List<GestureRow> _rows = [];
    private bool _loading;
    private string _optionSignature = "";
    private (string Gesture, string Kind, string? Side, int Target)? _pending;
    private int _pendingGeneration;
    private string? _hintMessage;
    /// <summary>已主动查询过手势状态的设备（页面打开/切设备时读一次，避免等待用户先改一次才显示）。</summary>
    private string? _gestureQueryBrand;
    /// <summary>缺值时的重试冷却时间（防止 stateChanged 每 1s 反复触发查询）。</summary>
    private DateTime _gestureQueryRetryAt;

    public ControlsPage()
    {
        InitializeComponent();
        _rows.AddRange(new[]
        {
            new GestureRow(LeftActionCombo, LeftActionRow, "long", "action", "left",
                FeatureModules.ControlsHoldActionLeft, s => s.HoldActionLeft, LongActionRaw),
            new GestureRow(RightActionCombo, RightActionRow, "long", "action", "right",
                FeatureModules.ControlsHoldActionRight, s => s.HoldActionRight, LongActionRaw),
            new GestureRow(InCallCombo, InCallRow, "long", "inCall", null,
                FeatureModules.ControlsHoldActionInCall, s => s.HoldActionInCall, LongInCallRaw),
            new GestureRow(DoubleLeftCombo, DoubleLeftRow, "double", "action", "left",
                FeatureModules.ControlsDoubleTapLeft, s => s.DoubleTapLeft, DoubleRaw),
            new GestureRow(DoubleRightCombo, DoubleRightRow, "double", "action", "right",
                FeatureModules.ControlsDoubleTapRight, s => s.DoubleTapRight, DoubleRaw),
            new GestureRow(DoubleInCallCombo, DoubleInCallRow, "double", "inCall", null,
                FeatureModules.ControlsDoubleTapInCall, s => s.DoubleTapInCall, DoubleInCallRaw),
            new GestureRow(TripleLeftCombo, TripleLeftRow, "triple", "action", "left",
                FeatureModules.ControlsTripleTapLeft, s => s.TripleTapLeft, TripleRaw),
            new GestureRow(TripleRightCombo, TripleRightRow, "triple", "action", "right",
                FeatureModules.ControlsTripleTapRight, s => s.TripleTapRight, TripleRaw),
            new GestureRow(MultiLeftCombo, MultiLeftRow, "multi", "action", "left",
                FeatureModules.ControlsMultiTapLeft, s => s.MultiTapLeft, MultiRaw),
            new GestureRow(MultiRightCombo, MultiRightRow, "multi", "action", "right",
                FeatureModules.ControlsMultiTapRight, s => s.MultiTapRight, MultiRaw),
            new GestureRow(LeftCycleCombo, LeftCycleRow, "long", "ancCycle", "left",
                FeatureModules.ControlsAncCycleLeft, s => s.AncCycleLeft, CycleRaw),
            new GestureRow(RightCycleCombo, RightCycleRow, "long", "ancCycle", "right",
                FeatureModules.ControlsAncCycleRight, s => s.AncCycleRight, CycleRaw),
        });
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
        _gestureQueryBrand = null;
        _gestureQueryRetryAt = DateTime.MinValue;
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

        // pending 解除判定必须先于所有渲染计算
        if (_pending is { } pending && PendingMatches(pending, state))
        {
            _pending = null;
            _pendingGeneration++;
            _hintMessage = null;
        }
        var isPending = _pending is not null;

        // 页面打开/切设备后主动读一次手势状态（会话初始化读可能因链路静默期被丢弃，
        // 否则要等用户改一次触发查询才显示）
        if (controllable && profile is not null)
        {
            MaybeQueryGesture(profile.Brand);
        }

        EnsureOptions(profile);

        var anyVisible = false;
        foreach (var row in _rows)
        {
            var avail = FeatureGate.Evaluate(row.ModuleId, profile, controllable);
            row.Row.Visibility = avail == FeatureAvailability.Unavailable ? Visibility.Collapsed : Visibility.Visible;
            anyVisible |= avail != FeatureAvailability.Unavailable;
            SetSelected(row, state);
            ApplyEnabled(row.Combo, avail, controllable, isPending);
        }

        ActionCard.Visibility = CardVisible("long", "action") || CardVisible("long", "inCall")
            ? Visibility.Visible : Visibility.Collapsed;
        DoubleTapCard.Visibility = CardVisible("double", "action") || CardVisible("double", "inCall")
            ? Visibility.Visible : Visibility.Collapsed;
        TripleTapCard.Visibility = CardVisible("triple", "action")
            ? Visibility.Visible : Visibility.Collapsed;
        MultiTapCard.Visibility = CardVisible("multi", "action")
            ? Visibility.Visible : Visibility.Collapsed;
        AncCycleCard.Visibility = CardVisible("long", "ancCycle")
            ? Visibility.Visible : Visibility.Collapsed;

        if (!anyVisible)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = Loc.Tr("Controls.NotSupported");
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

    private void MaybeQueryGesture(string brand)
    {
        // 已有完整数据则不重复查询；缺值时每 30s 允许重试一次（stateChanged 1s 刷一次也不打风暴）
        if (_gestureQueryBrand == brand && HasFullGestureState(brand))
        {
            return;
        }
        if (_gestureQueryBrand == brand && DateTime.UtcNow < _gestureQueryRetryAt)
        {
            return;
        }
        _gestureQueryBrand = brand;
        _gestureQueryRetryAt = DateTime.UtcNow.AddSeconds(30);
        _ = QueryGestureAsync(brand);
    }

    /// <summary>主动查询手势状态；回帧异步到达，等一轮后若仍有缺失则再查一次（最多两轮）。</summary>
    private async Task QueryGestureAsync(string brand)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await AgentHost.Instance.QueryGestureAsync(brand);
            }
            catch
            {
                return; // 链路未就绪：保留标记，30s 后由 stateChanged 重试
            }
            await Task.Delay(1500);
            if (HasFullGestureState(brand))
            {
                return;
            }
        }
    }

    private static bool HasFullGestureState(string brand)
    {
        var state = DeviceContext.Instance.GetState(brand);
        return state.HoldActionLeft is not null && state.HoldActionRight is not null
               && state.DoubleTapLeft is not null && state.DoubleTapRight is not null
               && state.TripleTapLeft is not null && state.TripleTapRight is not null
               && state.MultiTapLeft is not null && state.MultiTapRight is not null
               && state.AncCycleLeft is not null && state.AncCycleRight is not null;
    }

    private bool CardVisible(string gesture, string kind)
    {
        var profile = DeviceContext.Instance.Current;
        var state = profile is null ? null : DeviceContext.Instance.GetState(profile.Brand);
        var controllable = state is { IsControllable: true };
        return _rows.Any(r => r.Gesture == gesture && r.Kind == kind
                              && FeatureGate.Evaluate(r.ModuleId, profile, controllable) != FeatureAvailability.Unavailable);
    }

    /// <summary>下拉项只在 Profile 能力变化时重建（避免每次状态刷新重建控件）。</summary>
    private void EnsureOptions(DeviceProfile? profile)
    {
        var signature = string.Join('|', _rows.Select(r =>
            string.Join(',', OptionsFor(profile, r.Gesture, r.Kind))));
        if (signature == _optionSignature)
        {
            return;
        }
        _optionSignature = signature;

        _loading = true;
        foreach (var row in _rows)
        {
            var options = OptionsFor(profile, row.Gesture, row.Kind);
            row.Combo.ItemsSource = options
                .Select(tag => new Option(tag, LabelFor(row.Gesture, row.Kind, tag)))
                .ToList();
        }
        _loading = false;
    }

    private static List<string> OptionsFor(DeviceProfile? profile, string gesture, string kind)
    {
        var caps = profile?.Capabilities;
        return (gesture, kind) switch
        {
            ("long", "action") => caps?.HoldAction.Options ?? [],
            ("long", "inCall") => caps?.HoldAction.InCallOptions ?? [],
            ("long", "ancCycle") => caps?.HoldAction.AncCycleOptions.Select(v => v.ToString()).ToList() ?? [],
            ("double", "action") => caps?.DoubleTap.Options ?? [],
            ("double", "inCall") => caps?.DoubleTap.InCallOptions ?? [],
            ("triple", "action") => caps?.TripleTap.Options ?? [],
            ("multi", "action") => caps?.MultiTap.Options ?? [],
            _ => [],
        };
    }

    private static string LabelFor(string gesture, string kind, string tag)
    {
        if (kind == "ancCycle")
        {
            return int.TryParse(tag, out var cycle)
                ? (cycle is >= 1 and <= 4 ? Loc.Tr($"Controls.AncCycle.{cycle}") : tag)
                : tag;
        }
        var prefix = gesture switch
        {
            "long" => kind == "inCall" ? "InCall" : "Action",
            "double" => kind == "inCall" ? "DoubleInCall" : "DoubleTap",
            "triple" => "TripleTap",
            "multi" => "MultiTap",
            _ => "",
        };
        return Loc.Tr($"Controls.{prefix}.{tag}");
    }

    private void SetSelected(GestureRow row, DeviceRuntimeState? state)
    {
        _loading = true;
        var current = state is null ? null : row.ReadState(state);
        var tag = current is null
            ? null
            : row.Values.FirstOrDefault(kv => kv.Value == current.Value && kv.Key is not null).Key;
        row.Combo.SelectedValue = tag;
        _loading = false;
    }

    private static void ApplyEnabled(ComboBox combo, FeatureAvailability avail, bool controllable, bool pending)
    {
        combo.IsEnabled = avail == FeatureAvailability.Available && controllable && !pending;
        combo.Opacity = !controllable || avail == FeatureAvailability.Unknown || pending ? 0.4 : 1.0;
    }

    private async void OnGestureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { SelectedValue: string value } combo)
        {
            return;
        }
        var row = _rows.FirstOrDefault(r => r.Combo == combo);
        if (row is null || !row.Values.TryGetValue(value, out var target))
        {
            return;
        }
        await ApplyAsync(row, target, value);
    }

    private async Task ApplyAsync(GestureRow row, int target, string value)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null)
        {
            return;
        }

        // 进入 pending：所有下拉虚化锁定，直到耳机回报目标值（Refresh 检测）或 10s 超时
        _hintMessage = null;
        _pending = (row.Gesture, row.Kind, row.Side, target);
        var generation = ++_pendingGeneration;
        Refresh();
        try
        {
            await AgentHost.Instance.SetGestureAsync(brand, row.Gesture, row.Kind, row.Side ?? "left", value);
        }
        catch (Exception ex)
        {
            _pending = null;
            _pendingGeneration++;
            _hintMessage = ex.Message;
            Refresh();
            return;
        }
        _ = WatchPendingAsync(generation, brand);
    }

    /// <summary>pending 期间每 2s 查询一次手势状态，直到回报匹配或 10s 超时。</summary>
    private async Task WatchPendingAsync(int generation, string brand)
    {
        for (var i = 0; i < PendingTimeoutMs / 2000; i++)
        {
            await Task.Delay(2000);
            if (generation != _pendingGeneration || _pending is null)
            {
                return; // 已由回报解除
            }
            try
            {
                await AgentHost.Instance.QueryGestureAsync(brand);
            }
            catch
            {
                // 链路瞬断：继续轮询，最终超时提示
            }
        }

        if (generation != _pendingGeneration || _pending is null)
        {
            return;
        }
        _pending = null;
        _pendingGeneration++;
        _hintMessage = Loc.Tr("Controls.Timeout");
        Refresh();
    }

    private bool PendingMatches((string Gesture, string Kind, string? Side, int Target) pending, DeviceRuntimeState? state)
    {
        if (state is null)
        {
            return false;
        }
        var row = _rows.FirstOrDefault(r =>
            r.Gesture == pending.Gesture && r.Kind == pending.Kind && r.Side == pending.Side);
        return row is not null && row.ReadState(state) == pending.Target;
    }
}
