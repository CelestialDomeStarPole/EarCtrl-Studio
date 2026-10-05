using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EarCtrl.App.Views;
using EarCtrl.Core.StateMachine;
using WinForms = System.Windows.Forms;

namespace EarCtrl.App.Services;

/// <summary>
/// 托盘（M3）：左键开主窗，右键菜单 = 五态切换 + 显示主窗 + 退出。
/// WPF-UI 4.3 无内置托盘控件，用 WinForms NotifyIcon（MIT 侧，零 GPL 依赖）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private WinForms.NotifyIcon? _icon;
    private WinForms.ToolStripMenuItem? _ancMenu;
    private readonly Dictionary<AncFiveState, WinForms.ToolStripMenuItem> _stateItems = new();
    private SynchronizationContext? _ui;

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

        _ancMenu = new WinForms.ToolStripMenuItem(Loc.Tr("Tray.Anc"));
        foreach (var state in new[]
                 {
                     AncFiveState.Ultra, AncFiveState.General, AncFiveState.Cozy,
                     AncFiveState.Transparency, AncFiveState.Off,
                 })
        {
            var item = new WinForms.ToolStripMenuItem(TrayLabel(state))
            {
                CheckOnClick = true,
            };
            item.Click += async (_, _) => await ToggleStateAsync(state);
            _stateItems[state] = item;
            _ancMenu.DropDownItems.Add(item);
        }
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
        RefreshChecks();
    }

    private static string TrayLabel(AncFiveState state) => state switch
    {
        AncFiveState.Ultra => Loc.Tr("Anc.Ultra"),
        AncFiveState.General => Loc.Tr("Anc.General"),
        AncFiveState.Cozy => Loc.Tr("Anc.Cozy"),
        AncFiveState.Transparency => Loc.Tr("Anc.Transparency"),
        AncFiveState.Off => Loc.Tr("Anc.Off"),
        _ => Loc.Tr("Anc.Ultra"),
    };

    private async Task ToggleStateAsync(AncFiveState state)
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        if (brand is null) return;
        try
        {
            await AgentHost.Instance.SetAncModeAsync(brand, state);
        }
        catch
        {
            // 设备未就绪/下发失败：刷新勾选回到实际状态
        }
        RefreshChecks();
    }

    private void OnStateChanged(string brand) => _ui?.Post(_ => RefreshChecks(), null);
    private void OnCurrentChanged() => _ui?.Post(_ => RefreshChecks(), null);

    private void RefreshChecks()
    {
        var brand = DeviceContext.Instance.Current?.Brand;
        var current = brand is null ? AncFiveState.Unknown : DeviceContext.Instance.GetState(brand).Anc;
        foreach (var (state, item) in _stateItems)
        {
            item.Checked = state == current;
        }
    }

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
        if (_icon != null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
