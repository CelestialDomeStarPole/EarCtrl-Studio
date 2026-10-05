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
        Task.FromResult<IAgentSession>(new NamedPipeSession(profile.PipeName, config.AgentConnectTimeoutSec * 1000));
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
    /// <summary>链路被主动断开（App 内断开 / 系统蓝牙断开），设备回归前不得自愈拉回。</summary>
    private bool _suppressedConnect;

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
        // 名称必须命中本 Profile（多品牌并行时 detector 会广播给所有 supervisor）
        if (DeviceProfileRegistry.MatchByName([_profile], deviceName) is null) return;
        bool suppressed;
        lock (_gate) { _devicePresent = true; _deviceAddress = address; suppressed = _suppressedConnect; }
        if (!Enabled) { State = AgentState.Disabled; return; }

        if (State == AgentState.Ready)
        {
            // 设备回归：恢复此前被主动断开（系统蓝牙断开）的链路（C-2「等设备回归」语义）
            if (suppressed)
            {
                lock (_gate) _suppressedConnect = false;
                _awayTimer?.Dispose();
                _awayTimer = null;
                _ = Task.Run(ResumeConnectAsync);
            }
            return;
        }
        if (State is AgentState.AgentStarting or AgentState.Connecting) return;

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
        if (DeviceProfileRegistry.MatchByName([_profile], deviceName) is null) return;
        lock (_gate) { _devicePresent = false; _suppressedConnect = true; }
        // 设备离开：立即让 Agent 停止协议链路与自愈（用户在系统蓝牙断开不该被强行拉回），
        // 进程保留宽限期（A-6）等设备回归
        _ = Task.Run(async () =>
        {
            try
            {
                if (_session is { Connected: true })
                {
                    await _session.SendRequestAsync("disconnect", null, 3000);
                }
            }
            catch { /* 断开失败不阻塞 */ }
        });
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

    // ---- UI 动作（§3.1-C 手动控制） ----

    public bool IsReady => State == AgentState.Ready;

    /// <summary>UI 命令转发（setAncMode/setEq/…）；Agent 未连接时抛异常。</summary>
    public async Task<IpcResponse> SendAsync(string method, object? parameters = null, int timeoutMs = 8000)
    {
        var session = _session;
        if (session is not { Connected: true })
        {
            throw new InvalidOperationException("Agent 未连接");
        }
        return await session.SendRequestAsync(method, parameters, timeoutMs);
    }

    /// <summary>C-1 手动连接兜底：探测未命中时用已知地址直接拉起 + connect。</summary>
    public Task ManualConnectAsync(ulong address)
    {
        lock (_gate)
        {
            _devicePresent = true;
            _deviceAddress = address;
        }
        if (State is AgentState.Connecting or AgentState.AgentStarting)
        {
            return Task.CompletedTask;
        }
        if (State == AgentState.Ready)
        {
            // Ready 态用户仍可点「连接」：链路活着则 Agent 幂等回已连接；链路断（含自愈已放弃）则重连
            lock (_gate) _suppressedConnect = false;
            _awayTimer?.Dispose();
            _awayTimer = null;
            return ResumeConnectAsync();
        }
        _awayTimer?.Dispose();
        _awayTimer = null;
        State = AgentState.Detected;
        return LaunchAsync();
    }

    /// <summary>C-2 手动断开：断协议链路但保留 Agent 进程（阻止自愈拉回，直到手动连接/设备回归）。</summary>
    public async Task ManualDisconnectAsync()
    {
        lock (_gate) _suppressedConnect = true;
        if (_session is { Connected: true })
        {
            try { await _session.SendRequestAsync("disconnect", null, 5000); } catch { /* 断开失败不阻塞 */ }
        }
    }

    /// <summary>对已 Ready 的会话重发 connect（设备回归 / 手动恢复）。</summary>
    private async Task ResumeConnectAsync()
    {
        ulong? addr;
        lock (_gate) addr = _deviceAddress;
        var session = _session;
        if (session is not { Connected: true }) return;
        try
        {
            await session.SendRequestAsync("connect",
                addr is null ? null : new { deviceAddress = addr.Value.ToString("X12") }, 15000);
        }
        catch
        {
            // 恢复失败交给 L1/L2 兜底
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

            // §3.1-B：握手成功后用探测到的地址建立协议连接（connect 15s：RFCOMM 建链较慢）
            var connectParams = _deviceAddress is { } addr
                ? new { deviceAddress = addr.ToString("X12") }
                : null;
            var connect = await _session.SendRequestAsync("connect", connectParams, 15000);
            if (!connect.Ok)
            {
                throw new InvalidOperationException($"Agent 连接失败: {connect.Error?.Message}");
            }

            StartHealthMonitor();
            State = AgentState.Ready;
        }
        catch (Exception ex)
        {
            await CleanupSessionAsync();
            RecordRestart();
            if (State != AgentState.Disabled)
            {
                // connect 阶段失败（如 SPP 信道被占/耳机正被手机使用）不卡死 Failed：
                // 与 L1 同路径退避重试（1/2/5/10/30s），窗口内 ≥3 次进熔断
                State = AgentState.Reconnecting;
                _ = Task.Run(() => ScheduleRelaunch());
            }
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
