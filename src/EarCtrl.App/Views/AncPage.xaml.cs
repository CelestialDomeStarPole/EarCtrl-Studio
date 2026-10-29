using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Services;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.StateMachine;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

/// <summary>
/// 降噪页（K11）：模式三选（降噪/透传/关闭）+ 降噪等级下拉（仅降噪模式可用），
/// 作用于 K10 全局当前设备。页内模块按 Profile 能力裁剪（module id 见 <see cref="FeatureModules"/>）：
/// 未连接 = 全部显示但虚化，连接后不支持的直接隐藏（用户 2026-10-27 定调）。
/// </summary>
public partial class AncPage : Page
{
    private bool _suppressLevelChanged;
    private bool _suppressWindNoiseToggled;
    private bool _levelPending;          // setAncMode 飞行中：等级行虚化禁改（防快速切换请求堆积）
    private int _levelPendingGeneration; // 切档代际（守卫并发清除）
    /// <summary>切档完成后等级行/防风噪行的最短虚化保持时长（2B2A 确认 ~100ms，无保持则交叉虚化不可见）。</summary>
    private const int LevelPendingHoldMs = 800;

    // ---- 模式切换 pending（用户定调交互）：点击模式按钮后其它两个按钮虚化禁改，
    //      直到耳机确认新模式（Refresh 检测 state.mode == 点击的模式）或 8s 超时 ----
    private string? _modePendingTag;
    private int _modePendingGeneration;
    private const int ModePendingTimeoutMs = 8000;
    /// <summary>错误/超时提示（连接未就绪提示之外的消息；下次成功操作或状态收敛时清除）。</summary>
    private string? _hintMessage;

    // ---- 防风噪 pending（用户定调交互）：下发后虚化禁改，等耳机回报期望值才解锁；
    //      20s 上限（真机实测：状态真实变化后耳机对 2B B4 查询有 >10s 静默期），超时提示失败并解锁 ----
    private bool? _windNoisePendingTarget;
    private int _windNoisePendingGeneration;
    private const int WindNoisePendingTimeoutMs = 20000;
    /// <summary>pending 期间防风噪查询间隔（「2B70 使能 → 读请求」成对帧）。</summary>
    private const int WindNoiseQueryIntervalMs = 2000;

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

        // 模式切换 pending 解除判定（先于按钮渲染计算）：耳机确认的模式 == 用户点击的模式
        if (_modePendingTag is not null && ModeTag(mode) == _modePendingTag)
        {
            _modePendingTag = null;
            _modePendingGeneration++;
        }
        var modePendingTag = _modePendingTag;

        // ---- 页内模块能力门（Profile 为唯一权威；未连接 Unknown = 显示虚化，连接后 Unavailable = 隐藏）----
        var levelAvail = FeatureGate.Evaluate(FeatureModules.AncLevel, profile, controllable);
        var windAvail = FeatureGate.Evaluate(FeatureModules.AncWindNoise, profile, controllable);

        // 模式三按钮：按 anc.mode.* 裁剪；选中态跟随耳机真状态（华为原始字节：0=关闭 1=降噪 2=透传）；
        // 模式切换 pending 期间其它按钮虚化禁改（点击中的按钮保持可点）；
        // 切换等级 pending 期间（同为 2B04 写，模式+档位一起在途）三个按钮全部虚化禁改
        var modeButtons = new (Wpf.Ui.Controls.Button Button, string ModuleId, ColumnDefinition Column)[]
        {
            (BtnCancellation, FeatureModules.AncModeCancellation, ModeCol0),
            (BtnAwareness, FeatureModules.AncModeAwareness, ModeCol1),
            (BtnOff, FeatureModules.AncModeNormal, ModeCol2),
        };
        foreach (var (button, moduleId, column) in modeButtons)
        {
            var avail = FeatureGate.Evaluate(moduleId, profile, controllable);
            var hidden = avail == FeatureAvailability.Unavailable;
            button.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            column.Width = hidden ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

            var tag = (string)button.Tag;
            var selected = controllable && tag == ModeTag(mode);
            var locked = controllable &&
                         ((modePendingTag is not null && tag != modePendingTag) || _levelPending);
            button.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
            button.IsEnabled = avail == FeatureAvailability.Available && controllable && !locked;
            button.Opacity = avail == FeatureAvailability.Unknown || locked ? 0.4 : 1.0;
        }

        // 防风噪 pending 解除判定（必须先于 levelEnabled/windLocked 计算：否则确认帧到达的那次
        // Refresh 会用清除前的旧值渲染等级行，且此后无新事件再触发 Refresh → 等级行永久卡虚化）
        if (_windNoisePendingTarget.HasValue && state?.WindNoise == _windNoisePendingTarget.Value)
        {
            // 耳机已回报期望值——解除 pending，状态已收敛
            _windNoisePendingTarget = null;
            _windNoisePendingGeneration++;
            _hintMessage = null;
        }
        var windPending = _windNoisePendingTarget.HasValue;
        // 互相锁定：模式切换/切档飞行中或防风噪 pending 时，等级行与防风噪行同时虚化禁改——
        // 目的是在途写命令互斥（防止确认期间用户再下指令导致冲突），与切换方向无关
        var modePending = modePendingTag is not null;
        var windLocked = windPending || _levelPending || modePending;
        LevelPanel.Visibility = levelAvail == FeatureAvailability.Unavailable
            ? Visibility.Collapsed
            : Visibility.Visible;
        var levelEnabled = levelAvail == FeatureAvailability.Available && controllable && mode == 1 && !windLocked;
        _suppressLevelChanged = true;
        // 显示顺序（用户定调）：动态 / 轻度 / 均衡 / 深度（与协议 level 字节序无关）；
        // 每项按 Profile ancLevels 逐项裁剪（未连接 Unknown 时全部显示，连接后收敛）
        var options = new List<KeyValuePair<string, string>>();
        foreach (var (tag, moduleId, locKey) in new[]
                 {
                     ("dynamic", FeatureModules.AncLevelDynamic, "Anc.Level.Dynamic"),
                     ("comfort", FeatureModules.AncLevelComfort, "Anc.Level.Comfort"),
                     ("normal", FeatureModules.AncLevelNormal, "Anc.Level.Normal"),
                     ("ultra", FeatureModules.AncLevelUltra, "Anc.Level.Ultra"),
                 })
        {
            if (FeatureGate.Evaluate(moduleId, profile, controllable) != FeatureAvailability.Unavailable)
            {
                options.Add(new(tag, Loc.Tr(locKey)));
            }
        }
        LevelCombo.ItemsSource = options;
        LevelCombo.SelectedValue = LevelTag(state?.AncLevelRaw);
        _suppressLevelChanged = false;
        LevelPanel.Opacity = levelEnabled ? 1.0 : 0.4;
        LevelPanel.IsHitTestVisible = levelEnabled;
        LevelCombo.IsEnabled = levelEnabled;

        // 防风噪行（用户定调交互规则）：全局状态（所有非动态档共用）——
        // 未连接/能力未知时显示但虚化禁用；连接后不支持（Profile）则隐藏；
        // 支持时仅「降噪」模式显示，透传/关闭模式隐藏；动态档虚化且必开（不可点，切入时由 AgentHost 强制下发）；
        // 下发后 pending 虚化禁改，直到耳机回报期望值（20s 上限，期间每 2s 查询一次）
        var showWindNoise = windAvail != FeatureAvailability.Unavailable && (!controllable || mode == 1);
        WindNoisePanel.Visibility = showWindNoise ? Visibility.Visible : Visibility.Collapsed;
        if (showWindNoise)
        {
            var windKnownAvailable = windAvail == FeatureAvailability.Available;
            var dynamicLevel = controllable && state?.AncLevelRaw == 3;
            _suppressWindNoiseToggled = true;
            WindNoiseToggle.IsChecked = dynamicLevel || state?.WindNoise == true;
            WindNoiseToggle.IsEnabled = windKnownAvailable && controllable && !dynamicLevel && !windLocked;
            _suppressWindNoiseToggled = false;
            WindNoisePanel.Opacity = !windKnownAvailable || dynamicLevel || windLocked ? 0.4 : 1.0;
            WindNoisePanel.IsHitTestVisible = windKnownAvailable && !dynamicLevel && !windLocked;
        }

        if (!controllable)
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

        // 进入模式切换 pending：其它两个模式按钮虚化禁改，直到耳机确认新模式（Refresh 检测）或 8s 超时
        _hintMessage = null;
        _modePendingGeneration++;
        _modePendingTag = mode;
        var generation = _modePendingGeneration;
        Refresh();
        try
        {
            await AgentHost.Instance.SetAncModeAsync(brand, mode);
            ModePendingWatchdogAsync(generation); // async void 即发即忘
        }
        catch (Exception ex)
        {
            _modePendingTag = null;
            _modePendingGeneration++;
            _hintMessage = ex.Message;
            Refresh();
        }
    }

    /// <summary>模式切换 pending 超时（8s 上限）：耳机未确认新模式 → 提示失败并解锁。</summary>
    private async void ModePendingWatchdogAsync(int generation)
    {
        try
        {
            await Task.Delay(ModePendingTimeoutMs);
        }
        catch
        {
            return;
        }
        if (generation != _modePendingGeneration || _modePendingTag is null)
        {
            return; // 已被确认/新请求解除
        }
        _modePendingTag = null;
        _modePendingGeneration++;
        _hintMessage = Loc.Tr("Anc.SetTimeout");
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

        // 飞行中虚化等级行（防快速切换堆积 setAncMode → 2B2A pending 冲突重试）。
        // _levelPending = true 之后的所有代码都在 try 内：Refresh 若抛异常也要走 finally 清除，
        // 否则 _levelPending 卡 true → 等级行永久虚化
        _hintMessage = null;
        var generation = ++_levelPendingGeneration;
        _levelPending = true;
        try
        {
            Refresh();
            await AgentHost.Instance.SetAncModeAsync(brand, "cancellation", level);
            // 保持 ≥800ms 虚化：2B2A 确认很快（~100ms），无保持则交叉虚化一闪而过不可见；
            // 同时切档会让耳机短暂进入 2B B4 查询静默期，给状态收敛留时间
            await Task.Delay(LevelPendingHoldMs);
        }
        catch (Exception ex)
        {
            _hintMessage = ex.Message;
        }
        finally
        {
            // 代际守卫：仅最新一次切档负责解除（防御性，等级行禁用期间正常不会连切）
            if (generation == _levelPendingGeneration)
            {
                _levelPending = false;
                Refresh();
            }
        }
    }

    private async void OnWindNoiseToggled(object sender, RoutedEventArgs e)
    {
        // ToggleSwitch.Click 仅在用户点击时触发（程序化同步 IsChecked 不触发 Click），天然防回环；
        // _suppressWindNoiseToggled 仅作为防御性兜底保留
        if (_suppressWindNoiseToggled)
        {
            return;
        }
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null)
        {
            return;
        }

        // 进入 pending：虚化禁改，等耳机回报期望值（Refresh 检测）或 20s 超时解锁
        var target = WindNoiseToggle.IsChecked == true;
        _hintMessage = null;
        _windNoisePendingGeneration++;
        _windNoisePendingTarget = target;
        var generation = _windNoisePendingGeneration;
        Refresh();
        try
        {
            await AgentHost.Instance.SetWindNoiseAsync(brand, target);
            WindNoisePendingWatchdogAsync(brand, generation); // async void 即发即忘
        }
        catch (Exception ex)
        {
            _windNoisePendingTarget = null;
            _windNoisePendingGeneration++;
            _hintMessage = ex.Message;
            Refresh();
        }
    }

    /// <summary>
    /// 防风噪 pending 看门狗（20s 上限）：期间每 2s 发一次「2B70 使能 → 防风噪查询」成对帧
    /// （真机定论：查询必须 2B70 开路；状态真实变化后耳机有 >10s 查询静默期），
    /// 直到耳机回报期望值（Refresh 检测解除）或超时提示失败。
    /// </summary>
    private async void WindNoisePendingWatchdogAsync(string brand, int generation)
    {
        for (var i = 0; i < WindNoisePendingTimeoutMs / WindNoiseQueryIntervalMs; i++)
        {
            try
            {
                await Task.Delay(WindNoiseQueryIntervalMs);
            }
            catch
            {
                return;
            }
            if (generation != _windNoisePendingGeneration || !_windNoisePendingTarget.HasValue)
            {
                return; // 已被回报/新请求解除
            }
            try
            {
                await AgentHost.Instance.QueryWindNoiseAsync(brand);
            }
            catch
            {
                // 链路瞬断/Agent 未就绪：忽略，下一轮再试
            }
        }
        // 20s 到仍未解除 → 超时失败
        if (generation != _windNoisePendingGeneration || !_windNoisePendingTarget.HasValue)
        {
            return;
        }
        _windNoisePendingTarget = null;
        _windNoisePendingGeneration++;
        _hintMessage = Loc.Tr("Anc.SetTimeout");
        Refresh();
    }
}
