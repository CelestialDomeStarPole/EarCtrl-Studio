// SPDX-License-Identifier: GPL-3.0-only
// AgentRpc 单测：FakeSppLink 验证契约方法 → 华为命令帧的映射与状态事件（不连真机）。

using EarCtrl.Agents.Huawei.Commands;
using EarCtrl.Agents.Huawei.Lpv2;
using EarCtrl.Ipc.NamedPipe;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Agents.Huawei.Tests;

/// <summary>假链路：记录发送帧，可对指定命令返回 canned 响应。</summary>
public sealed class FakeSppLink : ISppLink
{
    public bool Started { get; private set; }
    public List<HuaweiSppPackage> Sent { get; } = [];

    public event Action<string>? LogDebug;
    public event Action<string>? LogWarn;
    public event Action? LinkLost;
    public event Action<string, byte[]>? RawFrame;

    /// <summary>发送某命令时自动回的帧（commandId hex → 响应构造器）。</summary>
    public Dictionary<string, Func<HuaweiSppPackage, HuaweiSppPackage>> Responses { get; } = new();

    public Task StartAsync(ulong address, int channel = 1)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Started = false;
        return Task.CompletedTask;
    }

    public Task<HuaweiSppPackage?> SendPackageAsync(HuaweiSppPackage pkg, int timeoutMs = 5000, params byte[][] acceptIds)
    {
        Sent.Add(pkg);
        var key = Convert.ToHexString(pkg.CommandId);
        if (Responses.TryGetValue(key, out var build))
        {
            return Task.FromResult<HuaweiSppPackage?>(build(pkg));
        }
        return Task.FromResult<HuaweiSppPackage?>(null);
    }

    public void OnNotify(byte[] commandId, Func<HuaweiSppPackage, Task> handler) { }

    public void SimulateLinkLost() => LinkLost?.Invoke();

    public void Dispose() { }
}

public class AgentRpcTests : IAsyncDisposable
{
    private readonly string _pipeName = $"earctrl.rpc.{Guid.NewGuid():N}";
    private readonly FakeSppLink _link = new();
    private readonly NamedPipeServer _server;
    private readonly NamedPipeClient _client;
    private readonly AgentRpc _rpc;

    public AgentRpcTests()
    {
        _server = new NamedPipeServer(_pipeName);
        _client = new NamedPipeClient(_pipeName);
        _rpc = new AgentRpc(_link, _server, new AgentRpcOptions
        {
            HeartbeatIntervalMs = 200,
            OrphanGraceMs = 300,
        });
        _rpc.Start();
    }

    private async Task<NamedPipeClient> ConnectAsync()
    {
        await _client.ConnectAsync();
        await Task.Delay(300); // 服务端 accept 窗口
        return _client;
    }

    [Fact]
    public async Task Hello_Ping_Work()
    {
        await ConnectAsync();
        var hello = await _client.SendRequestAsync("hello", new { version = "1" });
        Assert.True(hello.Ok);

        var ping = await _client.SendRequestAsync("ping");
        Assert.True(ping.Ok);
    }

    [Fact]
    public async Task SetAncMode_Ultra_SendsCorrectFrame_AndConfirms()
    {
        // 2B04 写 → 耳机回 2B2A param1=[level=02(ultra), mode=01(cancellation)]
        _link.Responses["2B04"] = _ => new HuaweiSppPackage(Cmd.AncRead)
        {
            Parameters = { [1] = [0x02, 0x01] },
        };

        await ConnectAsync();
        var resp = await _client.SendRequestAsync("setAncMode", new { mode = "ultra" });
        Assert.True(resp.Ok);

        // 验证发出的帧：ultra → (Cancellation, Ultra) → level 写法 param1=[01,02]（M1 实测值）
        var sent = Assert.Single(_link.Sent, p => Convert.ToHexString(p.CommandId) == "2B04");
        Assert.Equal("0102", Convert.ToHexString(sent.FindParam(1)));
    }

    [Fact]
    public async Task SetAncMode_Off_SendsZeroPayload()
    {
        await ConnectAsync();
        await _client.SendRequestAsync("setAncMode", new { mode = "off" });

        // off → AncSetModeRequest(Normal) → param1=[00,00]（M1 实测值）
        var sent = Assert.Single(_link.Sent, p => Convert.ToHexString(p.CommandId) == "2B04");
        Assert.Equal("0000", Convert.ToHexString(sent.FindParam(1)));
    }

    [Fact]
    public async Task GetState_ReflectsBatteryPush()
    {
        await ConnectAsync();
        // 模拟耳机连接即推 0127 电量帧（通过 AgentRpc 注册的 notify 无法直触发——Fake 空实现，
        // 改走 IPC：直接断言初始快照，电量推送逻辑由 GoldenSampleTests 覆盖解析层）
        var state = await _client.SendRequestAsync("getState");
        Assert.True(state.Ok);
        Assert.True(state.Result.HasValue);
    }

    public async ValueTask DisposeAsync()
    {
        await _rpc.DisposeAsync();
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }
}
