using System.Drawing;
using System.IO;
using System.Windows;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.StateMachine;
using WinForms = System.Windows.Forms;

namespace EarCtrl.App.Services;

/// <summary>
/// 托盘（M3）：左键开主窗，右键菜单 = 当前设备（K10）的降噪三模式 + 等级 + 显示/退出。
/// 与降噪页同一语义，并按 Profile 能力门裁剪（未连接 = 显示但禁用；不支持 = 隐藏）；切换设备时自动同步。
/// WPF-UI 4.3 无内置托盘控件，用 WinForms NotifyIcon（MIT 侧，零 GPL 依赖）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private WinForms.NotifyIcon? _icon;
    private WinForms.ToolStripMenuItem? _deviceItem;
    private WinForms.ToolStripMenuItem? _ancMenu;
    private WinForms.ToolStripMenuItem? _levelMenu;
    private WinForms.ToolStripSeparator? _levelSeparator;
    private readonly Dictionary<string, WinForms.ToolStripMenuItem> _modeItems = new();
    private readonly Dictionary<string, WinForms.ToolStripMenuItem> _levelItems = new();
    /// <summary>已提醒过低电量的设备（电量回升到阈值+5 以上时移除，避免重复弹气泡）。</summary>
    private readonly HashSet<string> _lowBatteryNotified = new();
    private SynchronizationContext? _ui;

    private static readonly (string Mode, string ModuleId, string LocKey)[] Modes =
    {
        ("cancellation", FeatureModules.AncModeCancellation, "Anc.Cancellation"),
        ("awareness", FeatureModules.AncModeAwareness, "Anc.Awareness"),
        ("normal", FeatureModules.AncModeNormal, "Anc.Off"),
    };

    /// <summary>等级项顺序（用户定调）：动态 / 轻度 / 均衡 / 深度。</summary>
    private static readonly (string Level, string ModuleId, string LocKey)[] Levels =
    {
        ("dynamic", FeatureModules.AncLevelDynamic, "Anc.Level.Dynamic"),
        ("comfort", FeatureModules.AncLevelComfort, "Anc.Level.Comfort"),
        ("normal", FeatureModules.AncLevelNormal, "Anc.Level.Normal"),
        ("ultra", FeatureModules.AncLevelUltra, "Anc.Level.Ultra"),
    };

    public void Initialize()
    {
        _ui = SynchronizationContext.Current;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "earctrl.ico");
        _icon = new WinForms.NotifyIcon
        {
            Text = Loc.Tr("App.Title"),
            Visible = true,
        };
        if (File.Exists(iconPath))
        {
            _icon.Icon = new Icon(iconPath);
        }

        var menu = new WinForms.ContextMenuStrip();

        // 多设备适配：菜单顶部常驻当前设备名（K10 全局当前设备），切换设备时刷新
        _deviceItem = new WinForms.ToolStripMenuItem("--") { Enabled = false };
        menu.Items.Add(_deviceItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _ancMenu = new WinForms.ToolStripMenuItem(Loc.Tr("Tray.Anc"));
        foreach (var (mode, _, locKey) in Modes)
        {
            var item = new WinForms.ToolStripMenuItem(Loc.Tr(locKey));
            var tag = mode;
            item.Click += async (_, _) => await SetModeAsync(tag);
            _modeItems[mode] = item;
            _ancMenu.DropDownItems.Add(item);
        }

        _levelSeparator = new WinForms.ToolStripSeparator();
        _ancMenu.DropDownItems.Add(_levelSeparator);
        _levelMenu = new WinForms.ToolStripMenuItem(Loc.Tr("Anc.Level"));
        foreach (var (level, _, locKey) in Levels)
        {
            var item = new WinForms.ToolStripMenuItem(Loc.Tr(locKey));
            var tag = level;
            item.Click += async (_, _) => await SetLevelAsync(tag);
            _levelItems[level] = item;
            _levelMenu.DropDownItems.Add(item);
        }
        _ancMenu.DropDownItems.Add(_levelMenu);
        menu.Items.Add(_ancMenu);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.Tr("Tray.Show"), null, (_, _) => ShowMainWindow());
        menu.Items.Add(Loc.Tr("Tray.Exit"), null, (_, _) => ExitApplication());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };

        DeviceContext.Instance.StateChanged += OnStateChanged;
        DeviceContext.Instance.CurrentChanged += OnCurrentChanged;
        DeviceContext.Instance.DevicesChanged += OnCurrentChanged;
        SyncMenu();
    }

    private async Task SetModeAsync(string mode)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null) return;
        try
        {
            await AgentHost.Instance.SetAncModeAsync(brand, mode);
        }
        catch
        {
            // 设备未就绪/下发失败：忽略，SyncMenu 回到实际状态
        }
        SyncMenu();
    }

    /// <summary>切等级 = 降噪模式 + 指定档位（与降噪页同语义；等级仅在降噪模式下可切换）。</summary>
    private async Task SetLevelAsync(string level)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null) return;
        try
        {
            await AgentHost.Instance.SetAncModeAsync(brand, "cancellation", level);
        }
        catch
        {
            // 同上
        }
        SyncMenu();
    }

    private void OnStateChanged(string brand) => _ui?.Post(_ =>
    {
        SyncMenu();
        CheckLowBattery(brand);
    }, null);

    private void OnCurrentChanged() => _ui?.Post(_ => SyncMenu(), null);

    /// <summary>低电量托盘气泡（M4）：任一耳电量 ≤ 阈值时每个设备只提醒一次（回升后重新武装）。
    /// 关盖在仓的电量 0 是协议占位，不参与判定（与主页显示规则一致）。</summary>
    private void CheckLowBattery(string brand)
    {
        if (!App.Config.LowBatteryNotify)
        {
            return;
        }
        var state = DeviceContext.Instance.GetState(brand);
        if (!state.IsControllable)
        {
            return;
        }

        static int? Usable(int? battery, bool? inCase, bool? lidClosed) =>
            battery is { } value && !(inCase == true && (lidClosed == true || value == 0))
                ? value
                : null;

        var levels = new[]
        {
            Usable(state.BatteryLeft, state.InCaseLeft, state.LidClosedLeft),
            Usable(state.BatteryRight, state.InCaseRight, state.LidClosedRight),
        };
        var level = levels.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(-1).Min();
        if (level < 0)
        {
            return;
        }

        var threshold = App.Config.LowBatteryThreshold;
        if (level <= threshold)
        {
            if (_lowBatteryNotified.Add(brand))
            {
                var name = DeviceContext.Instance.Devices.FirstOrDefault(d => d.Brand == brand)?.Model.Name
                           ?? brand;
                _icon?.ShowBalloonTip(3000, Loc.Tr("App.Title"),
                    $"{name} {Loc.Tr("Notify.LowBattery")} {level}%", WinForms.ToolTipIcon.Warning);
            }
        }
        else if (level > threshold + 5)
        {
            _lowBatteryNotified.Remove(brand);
        }
    }

    /// <summary>按当前设备的能力与真状态同步菜单：不支持隐藏、未连接显示但禁用、勾选跟随耳机。</summary>
    private void SyncMenu()
    {
        var context = DeviceContext.Instance;
        var profile = context.Current;
        var brand = profile?.Brand;
        var state = brand is null ? null : context.GetState(brand);
        var connected = state is { IsControllable: true };

        if (_deviceItem is not null)
        {
            _deviceItem.Text = profile?.Model.Name ?? "--";
        }

        var anyModeVisible = false;
        foreach (var (mode, moduleId, _) in Modes)
        {
            var item = _modeItems[mode];
            var avail = FeatureGate.Evaluate(moduleId, profile, connected);
            item.Available = avail != FeatureAvailability.Unavailable;
            item.Enabled = avail == FeatureAvailability.Available;
            item.Checked = item.Available && ModeTag(state?.AncModeRaw) == mode;
            anyModeVisible |= item.Available;
        }

        var levelAvail = FeatureGate.Evaluate(FeatureModules.AncLevel, profile, connected);
        var levelVisible = levelAvail != FeatureAvailability.Unavailable;
        foreach (var (level, moduleId, _) in Levels)
        {
            var item = _levelItems[level];
            var avail = FeatureGate.Evaluate(moduleId, profile, connected);
            item.Available = avail != FeatureAvailability.Unavailable;
            item.Enabled = avail == FeatureAvailability.Available && state?.AncModeRaw == 1;
            item.Checked = item.Available
                           && state?.AncModeRaw == 1
                           && LevelTag(state?.AncLevelRaw) == level;
        }
        if (_levelSeparator is not null)
        {
            _levelSeparator.Available = levelVisible;
        }
        if (_levelMenu is not null)
        {
            _levelMenu.Available = levelVisible;
            _levelMenu.Enabled = levelAvail == FeatureAvailability.Available && state?.AncModeRaw == 1;
        }
        if (_ancMenu is not null)
        {
            _ancMenu.Available = anyModeVisible;
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

    private static void ShowMainWindow()
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static void ExitApplication()
    {
        App.ForceShutdown = true;
        Application.Current?.Shutdown();
    }

    public void Dispose()
    {
        DeviceContext.Instance.StateChanged -= OnStateChanged;
        DeviceContext.Instance.CurrentChanged -= OnCurrentChanged;
        DeviceContext.Instance.DevicesChanged -= OnCurrentChanged;
        if (_icon != null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
