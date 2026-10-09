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
    /// <summary>动态档防风噪强制：记录上次降噪档位（边沿检测，切入动态档时下发一次强制开）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> _windNoiseLastLevel = new();

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

    /// <summary>当前设备下发防风噪开关（AncPage/动态档强制共用；IPC setWindNoise）。全局状态：所有非动态档共用。</summary>
    public async Task SetWindNoiseAsync(string brand, bool enabled)
    {
        var supervisor = GetSupervisor(brand) ?? throw new InvalidOperationException("设备未就绪");
        var response = await supervisor.SendAsync("setWindNoise", new { enabled });
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error?.Message ?? "setWindNoise 失败");
        }
    }

    /// <summary>当前设备查询防风噪状态（IPC queryWindNoise；「2B70 使能 → 读请求」成对，回报帧异步驱动 stateChanged）。</summary>
    public async Task QueryWindNoiseAsync(string brand)
    {
        var supervisor = GetSupervisor(brand) ?? throw new InvalidOperationException("设备未就绪");
        var response = await supervisor.SendAsync("queryWindNoise");
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error?.Message ?? "queryWindNoise 失败");
        }
    }

    /// <summary>
    /// 防风噪动态档强制（用户定调）：防风噪为全局状态，唯一例外是降噪+动态档必开——
    /// 切入动态档边沿时自动下发一次 setWindNoise(true)；切出再切回会再次触发。
    /// </summary>
    private void OnDeviceStateChanged(string brand)
    {
        var profile = GetProfile(brand);
        if (profile?.Capabilities.AncWindNoise != true) return;
        var state = Context.GetState(brand);
        var level = state.AncLevelRaw;
        var last = _windNoiseLastLevel.TryGetValue(brand, out var l) ? l : null;
        _windNoiseLastLevel[brand] = level;
        if (state.AncModeRaw == 1 && level == 3 && last != 3 && state.WindNoise != true)
        {
            _ = ForceWindNoiseOnAsync(brand);
        }
    }

    private async Task ForceWindNoiseOnAsync(string brand)
    {
        try
        {
            await SetWindNoiseAsync(brand, true);
        }
        catch
        {
            // Agent 未就绪/链路瞬断：忽略，下次切入动态档边沿再触发
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
