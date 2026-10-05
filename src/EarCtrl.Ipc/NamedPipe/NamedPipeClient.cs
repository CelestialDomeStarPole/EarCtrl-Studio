using System.IO.Pipes;
using System.Text;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Ipc.NamedPipe;

/// <summary>
/// NamedPipe 客户端（主程序侧，连接 Agent）。请求-响应关联 + 事件推送 + 断开检测（L1 主判定信号）。
/// </summary>
public sealed class NamedPipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<IpcResponse>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId = 1;
    private Task? _readLoop;

    /// <summary>收到 Agent 事件帧。</summary>
    public event Action<IpcEvent>? EventReceived;
    /// <summary>连接断开（L1 进程死亡主判定信号）。</summary>
    public event Action? Disconnected;
    /// <summary>任意帧到达（L2 心跳 freshness 用）。</summary>
    public event Action? AnyFrameReceived;

    public bool Connected { get; private set; }

    public NamedPipeClient(string pipeName) => _pipeName = pipeName;

    /// <summary>连接到 Agent。失败抛异常（由调用方决定重试）。timeoutMs：管道就绪等待上限（Agent 冷启动含 JIT+单实例探测，默认 15s）。</summary>
    public async Task ConnectAsync(int timeoutMs = 15000, CancellationToken ct = default)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(timeoutMs, ct);

        _reader = new StreamReader(_pipe, Encoding.UTF8);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
        Connected = true;

        _readLoop = Task.Run(() => ReadLoop(_cts.Token));
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line is null)
                {
                    break; // EOF = Agent 进程死亡（管道断开）
                }
                if (line.Length == 0) continue;

                AnyFrameReceived?.Invoke();
                var (kind, frame) = IpcFrames.Parse(line);
                switch (kind)
                {
                    case IpcFrameKind.Response:
                    {
                        var resp = (IpcResponse)frame;
                        lock (_pending)
                        {
                            if (_pending.Remove(resp.Id, out var tcs))
                            {
                                tcs.TrySetResult(resp);
                            }
                        }
                        break;
                    }
                    case IpcFrameKind.Event:
                        EventReceived?.Invoke((IpcEvent)frame);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Connected = false;
            // 唤醒所有等待者（以失败结束），避免悬挂
            lock (_pending)
            {
                foreach (var tcs in _pending.Values)
                {
                    tcs.TrySetException(new IpcConnectionLostException("Agent 管道已断开"));
                }
                _pending.Clear();
            }
            Disconnected?.Invoke();
        }
    }

    /// <summary>发起请求并等待响应。断开时抛 <see cref="IpcConnectionLostException"/>。</summary>
    public async Task<IpcResponse> SendRequestAsync(string method, object? parameters = null, int timeoutMs = 5000)
    {
        if (!Connected) throw new IpcConnectionLostException("尚未连接");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = tcs;
        }

        var request = new IpcRequest(id, method,
            parameters is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(parameters, IpcJson.Options));
        try
        {
            await SendLineAsync(IpcFrames.Serialize(request));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, _cts.Token));
            if (done != tcs.Task)
            {
                throw new TimeoutException($"IPC 请求超时: {method} ({timeoutMs}ms)");
            }
            return tcs.Task.Result;
        }
        finally
        {
            lock (_pending)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task SendLineAsync(string line)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _writer!.WriteLineAsync(line);
        }
        catch (IOException e)
        {
            throw new IpcConnectionLostException("写入失败，连接已断开", e);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_readLoop != null)
        {
            try { await _readLoop; } catch { /* 忽略停止期异常 */ }
        }
        // 管道可能已断开，dispose 时 flush 会抛异常，逐个安全释放
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>连接丢失（L1 信号对应的应用层异常）。</summary>
public sealed class IpcConnectionLostException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
