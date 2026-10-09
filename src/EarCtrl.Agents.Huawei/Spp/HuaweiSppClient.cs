// SPDX-License-Identifier: GPL-3.0-only
// 华为 SPP 客户端（借鉴 OpenFreebuds OfbDriverHuaweiGeneric 的收发与请求-响应关联设计，C# 原生重写）。
//
// 帧收发：'Z' 开头，读 4 字节头取长度，再补足整帧。
// 关联：请求 response_id == 请求 command_id；响应以同 command_id 唤醒等待者。
// 其余命令帧交给 NotifyHandler 推送（如 0127 电量变化、2b03 按键换档事件）。

using System.Collections.Concurrent;
using EarCtrl.Agents.Huawei.Lpv2;

namespace EarCtrl.Agents.Huawei;

/// <summary>华为 SPP 链路抽象（可注入 fake 做单测）。</summary>
public interface ISppLink : IDisposable
{
    bool Started { get; }
    Task StartAsync(ulong address, int channel = 1);
    Task StopAsync();
    Task<HuaweiSppPackage?> SendPackageAsync(HuaweiSppPackage pkg, int timeoutMs = 5000, params byte[][] acceptIds);
    void OnNotify(byte[] commandId, Func<HuaweiSppPackage, Task> handler);

    event Action<string>? LogDebug;
    event Action<string>? LogWarn;
    /// <summary>链路断开（L3 自愈触发信号）。</summary>
    event Action? LinkLost;
    /// <summary>原始帧（调试台 rawPacket 用）：dir = "TX"/"RX"。</summary>
    event Action<string, byte[]>? RawFrame;
}

public sealed class HuaweiSppClient : ISppLink
{
    private readonly Spp.RfcommTransport _transport = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HuaweiSppPackage>> _pending = new();
    private readonly ConcurrentDictionary<string, Func<HuaweiSppPackage, Task>> _notifyHandlers = new();
    /// <summary>发送互斥：RFCOMM 流写入必须串行，并发写会导致帧字节交织损坏。</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _recvCts;
    private Task? _recvTask;

    public event Action<string>? LogDebug;
    public event Action<string>? LogWarn;
    public event Action? LinkLost;
    public event Action<string, byte[]>? RawFrame;

    public bool Started { get; private set; }

    public void OnNotify(byte[] commandId, Func<HuaweiSppPackage, Task> handler) =>
        _notifyHandlers[Convert.ToHexString(commandId)] = handler;

    public async Task StartAsync(ulong address, int channel = 1)
    {
        await _transport.ConnectAsync(address, channel);
        _recvCts = new CancellationTokenSource();
        _recvTask = Task.Run(() => RecvLoop(_recvCts.Token));
        Started = true;
    }

    public async Task StopAsync()
    {
        Started = false;
        _recvCts?.Cancel();
        if (_recvTask != null)
        {
            // WinRT DataReader.LoadAsync 可能不响应取消（底层 native 操作不中断），
            // 无限等待会让孤儿退出半途夭折、留下「_cts 已取消但服务还活着」的僵尸进程——必须限时
            try { await _recvTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* 超时/取消/已结束都继续：dispose socket 强制断流收尾 */ }
        }
        _transport.Dispose();
    }

    /// <summary>
    /// 发送并等待响应；response_id 为空则发完即走（对应 send_package/_send_nowait）。
    /// acceptIds：可接受的其他响应帧命令 id（如电量读请求 0108 实机回 0127 推送帧）。
    /// </summary>
    public async Task<HuaweiSppPackage?> SendPackageAsync(HuaweiSppPackage pkg, int timeoutMs = 5000,
        params byte[][] acceptIds)
    {
        if (pkg.ResponseId.Length == 0)
        {
            await SendNowaitAsync(pkg);
            return null;
        }

        var keys = new[] { Convert.ToHexString(pkg.ResponseId) }
            .Concat(acceptIds.Select(Convert.ToHexString))
            .Distinct()
            .ToArray();

        if (keys.Any(_pending.ContainsKey))
        {
            LogDebug?.Invoke($"响应读取冲突 {keys[0]}，等待前置请求完成");
            // 与 Python 版一致：简单等待前置请求后重试
            await Task.Delay(500);
            return await SendPackageAsync(pkg, timeoutMs, acceptIds);
        }

        var tcs = new TaskCompletionSource<HuaweiSppPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var key in keys)
        {
            _pending[key] = tcs;
        }
        try
        {
            await SendNowaitAsync(pkg);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            foreach (var key in keys)
            {
                _pending.TryRemove(key, out _);
            }
            tcs.TrySetCanceled();
        }
    }

    private async Task SendNowaitAsync(HuaweiSppPackage pkg)
    {
        if (!Started) throw new InvalidOperationException("连接未建立，无法发送");
        var bytes = pkg.ToBytes();
        LogDebug?.Invoke($"TX {pkg}");
        RawFrame?.Invoke("TX", bytes);
        await _sendGate.WaitAsync();
        try
        {
            await _transport.TransportStream.WriteAsync(bytes);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ---- 接收循环（对应 _loop_recv/__recv_pacakge） ----

    private async Task RecvLoop(CancellationToken ct)
    {
        var stream = _transport.TransportStream;
        var buffer = new byte[512];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 读 4 字节头
                if (!await ReadExactAsync(stream, buffer.AsMemory(0, 4), ct))
                {
                    LogWarn?.Invoke("连接已关闭（读到空数据）");
                    LinkLost?.Invoke();
                    return;
                }
                if (buffer[0] != HuaweiSppPackage.HeaderByte) continue; // 丢弃非帧头

                var length = (buffer[1] << 8) | buffer[2];
                if (length < 4) continue;

                if (!await ReadExactAsync(stream, buffer.AsMemory(4, length), ct)) return;

                var raw = buffer.AsMemory(0, 4 + length).ToArray();
                HuaweiSppPackage pkg;
                try
                {
                    pkg = HuaweiSppPackage.FromBytes(raw);
                    LogDebug?.Invoke($"RX {pkg}");
                    RawFrame?.Invoke("RX", raw);
                }
                catch (Exception)
                {
                    LogWarn?.Invoke($"无法解析的包 {Convert.ToHexString(raw)}，忽略");
                    continue;
                }

                await DispatchAsync(pkg);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            LogWarn?.Invoke($"连接中断: {ex.Message}");
            LinkLost?.Invoke();
        }
        finally
        {
            Started = false;
        }
    }

    private async Task DispatchAsync(HuaweiSppPackage pkg)
    {
        var key = Convert.ToHexString(pkg.CommandId);
        if (_pending.TryGetValue(key, out var tcs))
        {
            tcs.TrySetResult(pkg);
        }
        else if (_notifyHandlers.TryGetValue(key, out var handler))
        {
            await handler(pkg);
        }
        else
        {
            LogWarn?.Invoke($"未注册的推送包 {pkg}");
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> target, CancellationToken ct)
    {
        var filled = 0;
        while (filled < target.Length)
        {
            var n = await stream.ReadAsync(target[filled..], ct);
            if (n == 0) return false;
            filled += n;
        }
        return true;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
