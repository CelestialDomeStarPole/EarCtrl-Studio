// SPDX-License-Identifier: GPL-3.0-only
// M2 集成测试：真实 NamedPipe 回环（MIT 的 EarCtrl.Ipc），验证 §4 契约帧收发、
// 请求-响应关联、事件推送、断开检测（L1 信号）。

using EarCtrl.Ipc.NamedPipe;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Agents.Huawei.Tests;

public class IpcRoundtripTests : IAsyncDisposable
{
    private readonly NamedPipeServer _server;
    private readonly NamedPipeClient _client;
    private readonly string _pipeName = $"earctrl.test.{Guid.NewGuid():N}";

    public IpcRoundtripTests()
    {
        _server = new NamedPipeServer(_pipeName);
        _client = new NamedPipeClient(_pipeName);
        _server.Start();
    }

    [Fact]
    public async Task RequestResponse_Roundtrip_Works()
    {
        var tcs = new TaskCompletionSource<IpcRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.RequestReceived += r => tcs.TrySetResult(r);

        var clientConnected = new TaskCompletionSource();
        _server.ClientConnected += () => clientConnected.TrySetResult();

        await _client.ConnectAsync();
        await clientConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var responseTask = _client.SendRequestAsync("ping", null, 5000);
        var request = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("ping", request.Method);
        await _server.SendAsync(IpcResponse.Success(request.Id, new { t = "ok" }));

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Ok);
    }

    [Fact]
    public async Task Events_PushedToClient()
    {
        var received = new TaskCompletionSource<IpcEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.EventReceived += e => received.TrySetResult(e);

        await _client.ConnectAsync();
        // 等服务端确认接入后再推事件，避免事件发到无客户端窗口
        await Task.Delay(500);
        await _server.SendAsync(new IpcEvent("stateChanged",
            System.Text.Json.JsonSerializer.SerializeToElement(new { anc = "ultra" }, IpcJson.Options)));

        var ev = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("stateChanged", ev.Event);
    }

    [Fact]
    public async Task ClientDisconnect_RaisesServerDisconnectedEvent()
    {
        var disconnected = new TaskCompletionSource();
        _server.ClientDisconnected += () => disconnected.TrySetResult();

        await _client.ConnectAsync();
        await Task.Delay(500); // 等服务端接入
        await _client.DisposeAsync();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }
}
