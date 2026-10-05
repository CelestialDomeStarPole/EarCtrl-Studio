// SPDX-License-Identifier: GPL-3.0-only
// Supervisor/Profile 单测（fake 注入，不真起进程、不连蓝牙）。
// 覆盖：《软件实现计划》§3/§3.1 的拉起握手、L1 断开重启、手动开关、L2 僵死 kill 重启、Profile 匹配。

using System.Diagnostics;
using EarCtrl.Core.Config;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.Supervision;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Agents.Huawei.Tests;

// ---- Profile 匹配 ----

public class ProfileMatchTests
{
    private static DeviceProfile HuaweiProfile() => new()
    {
        Brand = "huawei",
        AgentExe = "EarCtrl.Agent.Huawei.exe",
        PipeName = "earctrl.agent.huawei",
        Rules = new MatchRules { NamePrefixes = ["HUAWEI FreeBuds SE 4 ANC", "FreeBuds SE 4"] },
    };

    [Fact]
    public void MatchByName_HitsPrefix()
    {
        var profiles = new List<DeviceProfile> { HuaweiProfile() };
        Assert.NotNull(DeviceProfileRegistry.MatchByName(profiles, "HUAWEI FreeBuds SE 4 ANC"));
        Assert.NotNull(DeviceProfileRegistry.MatchByName(profiles, "FreeBuds SE 4 Lite(假设)"));
        Assert.Null(DeviceProfileRegistry.MatchByName(profiles, "iKF King Pro"));
    }

    [Fact]
    public void LoadAll_ParsesProfileFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"earctrl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "huawei.device.json"), """
            {
              "brand": "huawei",
              "agentExe": "EarCtrl.Agent.Huawei.exe",
              "pipeName": "earctrl.agent.huawei",
              "transport": "spp",
              "matchRules": { "namePrefixes": ["HUAWEI FreeBuds SE 4 ANC"] },
              "capabilities": { "ancModes": ["ultra","general","cozy","transparency","off"] }
            }
            """);

        var profiles = DeviceProfileRegistry.LoadAll(dir);
        var huawei = profiles.FirstOrDefault(p => p.Brand == "huawei");

        Assert.NotNull(huawei);
        Assert.Equal("earctrl.agent.huawei", huawei.PipeName);
        Assert.Equal("spp", huawei.Transport);
        Assert.Equal(5, huawei.Capabilities.AncModes.Count);
    }
}

// ---- Supervisor ----

/// <summary>受控假会话：hello 恒成功，可手动触发 Disconnected（L1）。</summary>
public sealed class FakeSession : IAgentSession
{
    public bool Connected { get; private set; }

    public event Action<IpcEvent>? EventReceived;
    public event Action? Disconnected;
    public event Action? AnyFrameReceived;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public Task<IpcResponse> SendRequestAsync(string method, object? parameters = null, int timeoutMs = 5000)
    {
        // 真实 Agent 收到 shutdown 会优雅退出 → 管道断开；fake 同步模拟该行为
        if (method == "shutdown")
        {
            Connected = false;
            Disconnected?.Invoke();
        }
        return Task.FromResult(IpcResponse.Success(1, new { version = "1" }));
    }

    public void SimulateDisconnect()
    {
        Connected = false;
        Disconnected?.Invoke();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeSessionFactory : IAgentSessionFactory
{
    public List<FakeSession> Sessions { get; } = [];

    public Task<IAgentSession> CreateAsync(DeviceProfile profile, AppConfig config)
    {
        var session = new FakeSession();
        Sessions.Add(session);
        return Task.FromResult<IAgentSession>(session);
    }
}

public sealed class FakeLauncher : IAgentProcessLauncher
{
    public int StartCount { get; private set; }

    public event Action<string, int?>? ProcessExited;

    public Task<Process> StartAsync(DeviceProfile profile, AppConfig config)
    {
        StartCount++;
        return Task.FromResult(new Process()); // 占位，测试中不会被真正 Kill 成功
    }
}

public class SupervisorTests : IAsyncDisposable
{
    private static DeviceProfile Profile() => new()
    {
        Brand = "test",
        AgentExe = "fake.exe",
        PipeName = $"earctrl.test.{Guid.NewGuid():N}",
        Rules = new MatchRules { NamePrefixes = ["Test Buds"] },
    };

    private static AppConfig Cfg() => new()
    {
        HeartbeatTimeoutSec = 1, // L2 测试用短超时
    };

    private static async Task WaitForStateAsync(AgentSupervisor s, AgentState target)
    {
        if (s.State == target) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.StateChanged += (_, e) => { if (e.State == target) tcs.TrySetResult(); };
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeviceAppeared_LaunchesAgent_ReachesReady()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessionFactory();
        await using var supervisor = new AgentSupervisor(Profile(), Cfg(), launcher, sessions);

        supervisor.OnDeviceAppeared("Test Buds", 0x1122334455);
        await WaitForStateAsync(supervisor, AgentState.Ready);

        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    public async Task SessionDisconnected_TriggersRelaunch()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessionFactory();
        await using var supervisor = new AgentSupervisor(Profile(), Cfg(), launcher, sessions);

        supervisor.OnDeviceAppeared("Test Buds", 0x1122334455);
        await WaitForStateAsync(supervisor, AgentState.Ready);

        // L1：管道断开 → Reconnecting → 重新拉起 → 再次 Ready
        sessions.Sessions[0].SimulateDisconnect();
        await WaitForStateAsync(supervisor, AgentState.Ready);

        Assert.Equal(2, launcher.StartCount);
    }

    [Fact]
    public async Task SetEnabledFalse_GoesDisabled_AndStopsSession()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessionFactory();
        await using var supervisor = new AgentSupervisor(Profile(), Cfg(), launcher, sessions);

        supervisor.OnDeviceAppeared("Test Buds", 0x1122334455);
        await WaitForStateAsync(supervisor, AgentState.Ready);

        supervisor.SetEnabled(false);
        await WaitForStateAsync(supervisor, AgentState.Disabled);
        Assert.False(sessions.Sessions[0].Connected);
    }

    [Fact]
    public async Task L2_SilentSession_KilledAndRelaunched()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessionFactory();
        await using var supervisor = new AgentSupervisor(Profile(), Cfg(), launcher, sessions);

        supervisor.OnDeviceAppeared("Test Buds", 0x1122334455);
        await WaitForStateAsync(supervisor, AgentState.Ready);

        // 假会话从不发帧 → L2 心跳超时（1s）→ kill + 重拉 → 新会话 Ready
        await WaitForStateAsync(supervisor, AgentState.Ready);
        await Task.Delay(2500); // 等 L2 计时器至少走一轮 + 退避重启
        Assert.True(launcher.StartCount >= 2, $"StartCount={launcher.StartCount}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
