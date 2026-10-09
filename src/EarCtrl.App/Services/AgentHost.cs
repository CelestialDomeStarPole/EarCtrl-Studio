using EarCtrl.Core.Config;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.StateMachine;
using EarCtrl.Core.Supervision;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.App.Services;

/// <summary>
/// 主程序侧装配（M3）：profiles 加载 → 已添加设备 → 探测器 + 各品牌 Supervisor → DeviceContext。
/// UI（页面/托盘）通过 Instance 访问：读 DeviceContext，动作经 GetSupervisor().SendAsync 下发。
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    public static AgentHost Instance { get; } = new();
    private AgentHost()
    {
        // 防风噪档位策略（AgentHost 生命周期 = App 生命周期，订阅一次即可）
        Context.StateChanged += OnDeviceStateChanged;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSupervisor> _supervisors = new();
    private IReadOnlyList<DeviceProfile> _profiles = [];
    private PollingDeviceDetector? _detector;
    private bool _started;
    /// <summary>防风噪策略边沿追踪：记录上次应用策略的降噪档位（null=非降噪模式），防止 stateChanged 回环。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> _windNoiseAppliedLevel = new();

    public DeviceContext Context => DeviceContext.Instance;

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        _profiles = DeviceProfileRegistry.LoadAll();
        var added = _profiles.Where(p => App.Config.AddedDevices.Contains(p.Brand)).ToList();
        Context.SetDevices(added);

        foreach (var profile in added)
        {
            CreateSupervisor(profile);
        }

        _detector = new PollingDeviceDetector();
        _detector.DeviceAppeared += (name, address) =>
        {
            foreach (var supervisor in _supervisors.Values)
            {
                supervisor.OnDeviceAppeared(name, address);
            }
        };
        _detector.DeviceDisappeared += name =>
        {
            foreach (var supervisor in _supervisors.Values)
            {
                supervisor.OnDeviceDisappeared(name);
            }
        };
        _detector.Start(added);
        await Task.CompletedTask;
    }

    private void CreateSupervisor(DeviceProfile profile)
    {
        lock (_gate)
        {
            if (_supervisors.ContainsKey(profile.Brand)) return;
            var supervisor = new AgentSupervisor(
                profile, App.Config, new AgentProcessLauncher(), new NamedPipeSessionFactory());
            supervisor.StateChanged += (_, e) => Context.UpdateSupervisorState(e.Brand, e.State);
            supervisor.AgentEvent += e => Context.UpdateAgentEvent(profile.Brand, e);
            _supervisors[profile.Brand] = supervisor;
        }
    }

    public AgentSupervisor? GetSupervisor(string brand)
    {
        lock (_gate) return _supervisors.GetValueOrDefault(brand);
    }

    public DeviceProfile? GetProfile(string brand) =>
        _profiles.FirstOrDefault(p => p.Brand == brand);

    /// <summary>已添加设备 + 名称命中但未添加的 Profile（供添加设备对话框）。</summary>
    public IReadOnlyList<DeviceProfile> AddableProfiles => _profiles
        .Where(p => Context.Devices.All(d => d.Brand != p.Brand))
        .ToList();

    /// <summary>添加设备：建 Supervisor + 入配置 + UI 出现（K10 设备区）。</summary>
    public void AddDevice(DeviceProfile profile)
    {
        CreateSupervisor(profile);
        Context.AddDevice(profile);
        if (!App.Config.AddedDevices.Contains(profile.Brand))
        {
            App.Config.AddedDevices.Add(profile.Brand);
            ConfigStore.Save(App.Config);
        }
    }

    /// <summary>移除设备（§3.1-C-3）：杀 Agent + 出配置。</summary>
    public async Task RemoveDeviceAsync(string brand)
    {
        AgentSupervisor? supervisor;
        lock (_gate) _supervisors.Remove(brand, out supervisor);
        if (supervisor != null)
        {
            supervisor.SetEnabled(false); // 黑名单语义：杀进程且不再自动拉起
            await supervisor.DisposeAsync();
        }
        App.Config.AddedDevices.Remove(brand);
        ConfigStore.Save(App.Config);
        Context.SetDevices(Context.Devices.Where(d => d.Brand != brand));
    }

    /// <summary>当前设备下发降噪模式/等级（AncPage/托盘共用；mode=normal|cancellation|awareness，level=normal|comfort|ultra|dynamic）。</summary>
    public async Task SetAncModeAsync(string brand, string mode, string? level = null)
    {
        var supervisor = GetSupervisor(brand) ?? throw new InvalidOperationException("设备未就绪");
        object payload = level is null ? new { mode } : new { mode, level };
        var response = await supervisor.SendAsync("setAncMode", payload);
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error?.Message ?? "setAncMode 失败");
        }
    }

    /// <summary>当前设备下发降噪五态（托盘菜单用；映射到 IPC mode/level）。</summary>
    public Task SetAncModeAsync(string brand, AncFiveState state) =>
        SetAncModeAsync(brand, AncFiveStateMapper.ToIpcMode(state));

    /// <summary>当前设备下发防风噪开关（AncPage/切档策略共用；IPC setWindNoise）。成功后写入当前档位的独立记忆。</summary>
    public async Task SetWindNoiseAsync(string brand, bool enabled)
    {
        var supervisor = GetSupervisor(brand) ?? throw new InvalidOperationException("设备未就绪");
        var response = await supervisor.SendAsync("setWindNoise", new { enabled });
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error?.Message ?? "setWindNoise 失败");
        }
        var level = Context.GetState(brand).AncLevelRaw;
        if (level is >= 0 and <= 2)
        {
            Context.GetState(brand).WindNoiseByLevel[level.Value] = enabled;
        }
    }

    /// <summary>
    /// 防风噪档位策略（用户定调的交互规则）：状态变化时按「降噪档位边沿」触发——
    /// 动态档(3)防风噪必开；轻度/均衡/深度各自记忆独立开关状态，切档自动应用记忆值；
    /// 首次遇到某档位时记录耳机当前状态作为该档初始值。仅降噪模式下管理。
    /// </summary>
    private void OnDeviceStateChanged(string brand)
    {
        var profile = GetProfile(brand);
        if (profile?.Capabilities.AncWindNoise != true) return;
        var state = Context.GetState(brand);
        var level = state.AncLevelRaw;
        if (state.AncModeRaw != 1 || level is null)
        {
            _windNoiseAppliedLevel[brand] = null;
            return;
        }
        if (_windNoiseAppliedLevel.TryGetValue(brand, out var last) && last == level) return;
        _windNoiseAppliedLevel[brand] = level;
        _ = ApplyWindNoisePolicyAsync(brand, level.Value);
    }

    private async Task ApplyWindNoisePolicyAsync(string brand, int level)
    {
        var state = Context.GetState(brand);
        bool target;
        if (level == 3)
        {
            target = true; // 动态档防风噪必开
        }
        else if (state.WindNoiseByLevel.TryGetValue(level, out var cached))
        {
            target = cached;
        }
        else
        {
            // 首次遇到该档位：以耳机当前状态作为初始记忆
            if (state.WindNoise is { } current) state.WindNoiseByLevel[level] = current;
            return;
        }
        if (state.WindNoise == target) return;
        try
        {
            await SetWindNoiseAsync(brand, target);
        }
        catch
        {
            // Agent 未就绪/链路瞬断：忽略，下次档位边沿重新应用
        }
    }

    /// <summary>手动连接兜底（§3.1-C-1）：扫描已配对设备拿地址后直接拉起 + connect。</summary>
    public async Task<string?> ManualConnectAsync(string brand)
    {
        var profile = GetProfile(brand);
        var supervisor = GetSupervisor(brand);
        if (profile is null || supervisor is null) return "设备不存在";

        var paired = await PairedDeviceScanner.ScanAsync([profile]);
        var hit = paired.FirstOrDefault(d => d.Profile is not null);
        if (hit is null)
        {
            return Loc.Tr("Status.PairedNotFound");
        }
        await supervisor.ManualConnectAsync(hit.Address);
        return null; // null = 成功发起
    }

    public Task ManualDisconnectAsync(string brand) =>
        GetSupervisor(brand)?.ManualDisconnectAsync() ?? Task.CompletedTask;

    /// <summary>轮询间隔变更（设置页）：对所有在跑 Agent 热更新；未就绪的 Agent 下次拉起以命令行初值生效。</summary>
    public async Task ApplyPollIntervalAsync(int seconds)
    {
        var ms = Math.Max(0, seconds) * 1000;
        List<AgentSupervisor> supervisors;
        lock (_gate) supervisors = _supervisors.Values.ToList();
        foreach (var supervisor in supervisors)
        {
            try
            {
                await supervisor.SendAsync("setPollMs", new { ms });
            }
            catch
            {
                // Agent 未就绪/不支持：忽略——下次拉起时命令行 --poll 会带最新值
            }
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
        }
        if (_detector != null)
        {
            await _detector.StopAsync();
            _detector = null;
        }
        List<AgentSupervisor> supervisors;
        lock (_gate)
        {
            supervisors = _supervisors.Values.ToList();
            _supervisors.Clear();
        }
        foreach (var supervisor in supervisors)
        {
            await supervisor.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
