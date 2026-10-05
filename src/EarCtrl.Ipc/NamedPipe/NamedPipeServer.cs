using System.IO.Pipes;
using System.Text;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Ipc.NamedPipe;

/// <summary>
/// 单客户端 NamedPipe 服务端（Agent 侧）。NDJSON 帧收发：
/// 接受一个客户端连接，读循环逐行解析分发；断开后自动等待下一个客户端（单实例幂等的复用语义）。
/// </summary>
public sealed class NamedPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _acceptLoop;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>收到请求帧。</summary>
    public event Action<IpcRequest>? RequestReceived;
    /// <summary>客户端接入。</summary>
    public event Action? ClientConnected;
    /// <summary>当前客户端断开（无客户端时也可能在等待连接）。</summary>
    public event Action? ClientDisconnected;

    public bool HasClient { get; private set; }

    public NamedPipeServer(string pipeName) => _pipeName = pipeName;

    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await _pipe.WaitForConnectionAsync(ct);

                _reader = new StreamReader(_pipe, Encoding.UTF8);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                HasClient = true;
                ClientConnected?.Invoke();
                await ReadLoop(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // 客户端断开，回到等待下一个连接
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                HasClient = false;
                ClientDisconnected?.Invoke();
                CleanupClient();
            }
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && HasClient)
        {
            var line = await _reader!.ReadLineAsync(ct);
            if (line is null)
            {
                return; // EOF → 客户端断开
            }
            if (line.Length == 0)
            {
                continue;
            }

            var (kind, frame) = IpcFrames.Parse(line);
            if (kind == IpcFrameKind.Request)
            {
                RequestReceived?.Invoke((IpcRequest)frame);
            }
            // 服务端不应收到 Response/Event；忽略即可（防御式）
        }
    }

    /// <summary>发送一帧（Response 或 Event）。未连接时静默丢弃（事件语义）。</summary>
    public async Task SendAsync(object frame)
    {
        if (!HasClient || _writer is null) return;
        var line = IpcFrames.Serialize(frame);
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(line);
        }
        catch (IOException)
        {
            // 写失败 = 客户端已断开；读循环会处理清理
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void CleanupClient()
    {
        // 客户端断开时 writer.Flush 可能抛异常（closed pipe），逐个安全释放
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_acceptLoop != null)
        {
            try { await _acceptLoop; } catch { /* 忽略停止期异常 */ }
        }
        CleanupClient();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
