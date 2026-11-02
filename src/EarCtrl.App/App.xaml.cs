using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using EarCtrl.App.Services;
using EarCtrl.Core.Config;

namespace EarCtrl.App;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();

    /// <summary>托盘「退出」置位后允许窗口真正关闭并结束进程（默认关窗 = 最小化到托盘）。</summary>
    public static bool ForceShutdown { get; set; }

    private TrayService? _tray;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showWindowEvent;
    private Thread? _showWindowWatcher;

    /// <summary>第二实例请求第一实例显示主窗口的命名事件（静默驻留托盘时也能被双击图标唤起）。</summary>
    private const string ShowWindowEventName = @"Local\EarCtrl.App.ShowWindow";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 静默驻留（无窗口）时不能因「最后一个窗口关闭」而退出；退出统一走托盘菜单的显式 Shutdown
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 单实例互斥：重复启动 → 激活已有实例主窗口后退出（防双 Supervisor 抢同一 Agent/耳机）
        _instanceMutex = new Mutex(true, @"Local\EarCtrl.App.Instance", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            RequestExistingInstanceShowWindow();
            Shutdown();
            return;
        }

        Config = ConfigStore.Load();
        Loc.SetCulture(Config.Language);

        await AgentHost.Instance.StartAsync();

        var window = new MainWindow();
        ThemeService.Apply(Config.Theme);
        _tray = new TrayService();
        _tray.Initialize();
        StartShowWindowWatcher();

        // 开机自启（--autostart）静默驻留托盘；手动启动才打开主界面
        if (!e.Args.Contains(AutoStartService.SilentArgument, StringComparer.OrdinalIgnoreCase))
        {
            window.Show();
        }
    }

    /// <summary>监听第二实例的显示请求：从托盘驻留状态唤起主窗口。</summary>
    private void StartShowWindowWatcher()
    {
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowWatcher = new Thread(() =>
        {
            while (true)
            {
                _showWindowEvent.WaitOne();
                Dispatcher.BeginInvoke(TrayService.ShowMainWindow);
            }
        })
        { IsBackground = true };
        _showWindowWatcher.Start();
    }

    /// <summary>已有实例在跑：优先发事件让它显示主窗口；旧版本实例无事件则回退到窗口句柄激活。</summary>
    private static void RequestExistingInstanceShowWindow()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
            showEvent.Set();
            return;
        }
        catch
        {
            // 事件不存在（旧版本实例）：回退
        }
        ActivateExistingWindow();
    }

    private static void ActivateExistingWindow()
    {
        var existing = Process.GetProcessesByName("EarCtrl.App")
            .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
        if (existing is null) return;
        ShowWindow(existing.MainWindowHandle, SW_RESTORE);
        SetForegroundWindow(existing.MainWindowHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    protected override async void OnExit(ExitEventArgs e)
    {
        ConfigStore.Save(Config);
        _tray?.Dispose();
        await AgentHost.Instance.StopAsync();
        base.OnExit(e);
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { /* 异常路径可能未持有 */ }
        }
        _instanceMutex?.Dispose();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 兜底避免闪退；结构化日志后续里程碑引入
        MessageBox.Show(e.Exception.ToString(), "EarCtrl Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
