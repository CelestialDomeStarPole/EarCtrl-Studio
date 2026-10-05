using EarCtrl.Ipc.NamedPipe;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Core.Supervision;

/// <summary>
/// Agent 会话抽象（Supervisor 与 NamedPipe 解耦，单测可注入 fake）。
/// 语义与 <see cref="NamedPipeClient"/> 一致：请求-响应 + 事件 + 断开信号（L1）。
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    bool Connected { get; }
    event Action<IpcEvent>? EventReceived;
    event Action? Disconnected;
    event Action? AnyFrameReceived;

    Task ConnectAsync(CancellationToken ct = default);
    Task<IpcResponse> SendRequestAsync(string method, object? parameters = null, int timeoutMs = 5000);
}

/// <summary>NamedPipe 会话（真实实现）。</summary>
public sealed class NamedPipeSession : IAgentSession
{
    private readonly NamedPipeClient _client;

    public NamedPipeSession(string pipeName, int connectTimeoutMs = 15000)
    {
        _client = new NamedPipeClient(pipeName);
        _connectTimeoutMs = connectTimeoutMs;
    }

    private readonly int _connectTimeoutMs;

    public bool Connected => _client.Connected;

    public event Action<IpcEvent>? EventReceived
    {
        add => _client.EventReceived += value;
        remove => _client.EventReceived -= value;
    }

    public event Action? Disconnected
    {
        add => _client.Disconnected += value;
        remove => _client.Disconnected -= value;
    }

    public event Action? AnyFrameReceived
    {
        add => _client.AnyFrameReceived += value;
        remove => _client.AnyFrameReceived -= value;
    }

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(_connectTimeoutMs, ct);

    public Task<IpcResponse> SendRequestAsync(string method, object? parameters = null, int timeoutMs = 5000) =>
        _client.SendRequestAsync(method, parameters, timeoutMs);

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}
