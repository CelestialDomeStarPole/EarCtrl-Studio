using System.Diagnostics;
using EarCtrl.Core.Config;
using EarCtrl.Core.Profiles;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Core.Supervision;

/// <summary>Agent 状态机状态（§3 状态机）。</summary>
public enum AgentState
{
    Idle,
    Detected,
    AgentStarting,
    Connecting,
    Ready,
    Reconnecting,
    Failed,
    Cooldown,
    Disabled,
}

/// <summary>状态/事件通知（M3 的 UI 绑定这些）。</summary>
public sealed record AgentStateChangedEventArgs(string Brand, AgentState State, string? Error = null);

/// <summary>会话工厂抽象（单测注入 fake；真实实现连 NamedPipe）。</summary>
public interface IAgentSessionFactory
{
    Task<IAgentSession> CreateAsync(DeviceProfile profile, AppConfig config);
}

public sealed class NamedPipeSessionFactory : IAgentSessionFactory
{
    public Task<IAgentSession> CreateAsync(DeviceProfile profile, AppConfig config) =>
        Task.FromResult<IAgentSession>(new NamedPipeSession(profile.PipeName));
}

/// <summary>
/// 单品牌 Agent 监督器（§3/§3.1 全逻辑）：
/// 探测命中 → 拉起 → IPC 握手 → Ready；L1 管道断开重启 / L2 心跳僵死 kill+重启 /
/// L3 由 Agent 自愈（本层只观察 connectionChanged）；熔断 / 崩溃留证 / 手动开关。
/// </summary>
public sealed class AgentSupervisor : IAsyncDisposable
{
    private readonly DeviceProfile _profile;
    private readonly AppConfig _config;
    private readonly IAgentProcessLauncher _launcher;
    private readonly IAgentSessionFactory _sessionFactory;
    private readonly object _gate = new();

    private AgentState _state = AgentState.Idle;
    private Process? _process;
    private IAgentSession? _session;
    private DateTime _lastFrameUtc = DateTime.UtcNow;
    private Timer? _healthTimer;
    private Timer? _awayTimer;
    private readonly Queue<DateTime> _restartTimestamps = new();
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private ulong? _deviceAddress;
    private bool _devicePresent;

    public string Brand => _profile.Brand;
    public AgentState State { get => _state; private set { _state = value; StateChanged?.Invoke(this, new AgentStateChangedEventArgs(Brand, value)); } }
    public bool Enabled { get; private set; } = true;

    public event Action<object, AgentStateChangedEventArgs>? StateChanged;
    public event Action<IpcEvent>? AgentEvent;

    public AgentSupervisor(DeviceProfile profile, AppConfig config,
        IAgentProcessLauncher launcher, IAgentSessionFactory sessionFactory)
    {
        _profile = profile;
        _config = config;
        _launcher = launcher;
        _sessionFactory = sessionFactory;
        _launcher.ProcessExited += OnProcessExited;
    }

    // ---- 探测回调（由 detector 驱动） ----

    public void OnDeviceAppeared(string deviceName, ulong address)
    {
        lock (_gate) { _devicePresent = true; _deviceAddress = address; }
        if (!Enabled) { State = AgentState.Disabled; return; }
        if (State is AgentState.Ready or AgentState.AgentStarting or AgentState.Connecting) return;

        if (DateTime.UtcNow < _cooldownUntilUtc)
        {
            State = AgentState.Cooldown;
            return;
        }

        State = AgentState.Detected;
        _awayTimer?.Dispose();
        _awayTimer = null;
        _ = Task.Run(() => LaunchAsync());
    }

    public void OnDeviceDisappeared(string deviceName)
    {
        lock (_gate) { _devicePresent = false; }
        // A-6：宽限期内 Agent 继续服务，超时才 shutdown
        _awayTimer?.Dispose();
        _awayTimer = new Timer(_ =>
        {
            if (!_devicePresent && State is AgentState.Ready or AgentState.Reconnecting)
            {
                _ = Task.Run(async () =>
                {
                    await ShutdownSessionAsync();
                    State = AgentState.Idle;
                });
            }
        }, null, _config.DeviceAwayTimeoutSec * 1000, Timeout.Infinite);
    }

    // ---- 手动开关（§3.1-C） ----

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            _ = Task.Run(async () =>
            {
                await ShutdownSessionAsync();
                State = AgentState.Disabled;
            });
        }
        else if (_devicePresent)
        {
            State = AgentState.Detected;
            _ = Task.Run(() => LaunchAsync());
        }
        else
        {
            State = AgentState.Idle;
        }
    }

    // ---- 拉起与握手 ----

    private async Task LaunchAsync()
    {
        if (!Enabled) return;
        State = AgentState.AgentStarting;

        try
        {
            _process = await _launcher.StartAsync(_profile, _config);
            State = AgentState.Connecting;
            _session = await _sessionFactory.CreateAsync(_profile, _config);

            _session.EventReceived += e => { AgentEvent?.Invoke(e); };
            _session.Disconnected += OnSessionDisconnected;
            _session.AnyFrameReceived += () =>
            {
                _lastFrameUtc = DateTime.UtcNow;
            };

            await _session.ConnectAsync();
            var hello = await _session.SendRequestAsync("hello",
                new { version = "1", profileHash = _profile.Brand },
                _config.ShutdownGraceSec * 1000 + 2000);
            if (!hello.Ok)
            {
                throw new InvalidOperationException($"Agent 握手失败: {hello.Error?.Message}");
            }

            StartHealthMonitor();
            State = AgentState.Ready;
        }
        catch (Exception ex)
        {
            await CleanupSessionAsync();
            RecordRestart();
            if (State != AgentState.Disabled) State = AgentState.Failed;
            Debug.WriteLine($"[{Brand}] Agent 启动失败: {ex.Message}");
        }
    }

    // ---- L1/L2 健康（§3.1-D） ----

    private void StartHealthMonitor()
    {
        _healthTimer?.Dispose();
        _lastFrameUtc = DateTime.UtcNow;
        _healthTimer = new Timer(_ =>
        {
            if (State != AgentState.Ready) return;
            var silence = (DateTime.UtcNow - _lastFrameUtc).TotalMilliseconds;
            if (silence > _config.HeartbeatTimeoutSec * 1000)
            {
                // L2 链路僵死：kill + 重启（进程死亡会触发 Disconnected → 重启路径）
                RecordRestart();
                KillProcess();
                _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _session = null;
                State = AgentState.Reconnecting;
                _ = Task.Run(() => ScheduleRelaunch());
            }
        }, null, _config.HeartbeatTimeoutSec * 1000, _config.HeartbeatTimeoutSec * 1000);
    }

    private void OnSessionDisconnected()
    {
        // L1 管道断开 = Agent 死亡主判定信号
        if (State is AgentState.Disabled or AgentState.Idle) return;
        if (_shutdownRequested) return;

        RecordRestart();
        State = AgentState.Reconnecting;
        _ = Task.Run(() => ScheduleRelaunch());
    }

    private void ScheduleRelaunch()
    {
        // 指数退避 1/2/5/10/30s 封顶；熔断窗口内 ≥3 次重启 → 60s 冷却
        var now = DateTime.UtcNow;
        lock (_restartTimestamps)
        {
            while (_restartTimestamps.Count > 0 &&
                   (now - _restartTimestamps.Peek()).TotalMinutes >= 5)
            {
                _restartTimestamps.Dequeue();
            }
            if (_restartTimestamps.Count >= _config.RestartLimit)
            {
                _cooldownUntilUtc = now.AddSeconds(_config.CircuitCooldownSec);
                _restartTimestamps.Clear();
                State = AgentState.Cooldown;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_config.CircuitCooldownSec * 1000);
                    if (_devicePresent && State == AgentState.Cooldown && Enabled)
                    {
                        State = AgentState.Detected;
                        await LaunchAsync();
                    }
                    else if (State == AgentState.Cooldown)
                    {
                        State = AgentState.Idle;
                    }
                });
                return;
            }
        }

        var delayMs = Math.Min(30000, (int)Math.Pow(2, Math.Max(0, _restartTimestamps.Count - 1)) * 1000);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            if (_devicePresent && Enabled && !_shutdownRequested)
            {
                await LaunchAsync();
            }
            else
            {
                State = _devicePresent ? AgentState.Detected : AgentState.Idle;
            }
        });
    }

    private void RecordRestart()
    {
        lock (_restartTimestamps)
        {
            _restartTimestamps.Enqueue(DateTime.UtcNow);
        }
        // 崩溃留证（§3.1-D）：异常重启计数写入本地日志
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EarCtrl");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, $"agent-{Brand}-last.log"),
                $"{DateTime.UtcNow:O} restart recorded, state={State}{Environment.NewLine}");
        }
        catch { /* 日志失败不阻塞 */ }
    }

    // ---- 进程退出观察 ----

    private void OnProcessExited(string brand, int? exitCode)
    {
        if (brand != Brand) return;
        if (exitCode is not (null or 0))
        {
            // 异常退出留证
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EarCtrl");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, $"agent-{Brand}-last.log"),
                    $"{DateTime.UtcNow:O} abnormal exit code={exitCode}{Environment.NewLine}");
            }
            catch { /* 忽略 */ }
        }
    }

    // ---- 关停 ----

    private bool _shutdownRequested;

    private async Task ShutdownSessionAsync()
    {
        _shutdownRequested = true;
        _healthTimer?.Dispose();
        try
        {
            if (_session is { Connected: true })
            {
                await _session.SendRequestAsync("shutdown", null, _config.ShutdownGraceSec * 1000);
                await Task.Delay(_config.ShutdownGraceSec * 1000);
            }
        }
        catch { /* 断开/超时都不阻塞 */ }
        KillProcess();
        await CleanupSessionAsync();
        _shutdownRequested = false;
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* 进程可能已退出 */ }
    }

    private async Task CleanupSessionAsync()
    {
        _healthTimer?.Dispose();
        if (_session != null)
        {
            _session.Disconnected -= OnSessionDisconnected;
            await _session.DisposeAsync();
            _session = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownSessionAsync();
        _awayTimer?.Dispose();
        _launcher.ProcessExited -= OnProcessExited;
    }
}
