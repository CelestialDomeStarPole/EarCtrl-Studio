// SPDX-License-Identifier: GPL-3.0-only
// AgentRpc：《软件实现计划》§4 IPC 契约的华为 Agent 实现。
// - 方法：hello/ping/shutdown/connect/disconnect/getState/setAncMode/setEq/setLowLatency/debug*
// - 事件：stateChanged / connectionChanged / heartbeat / rawPacket / log
// - 生命周期：单实例幂等由 Program 处理；此处负责 heartbeat（静默期 30s）、孤儿退出（无客户端 30s）、L3 链路自愈。

using System.Text.Json;
using EarCtrl.Agents.Huawei.Commands;
using EarCtrl.Agents.Huawei.Lpv2;
using EarCtrl.Ipc.NamedPipe;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Agents.Huawei;

public sealed class AgentRpc : IAsyncDisposable
{
    private readonly ISppLink _link;
    private readonly NamedPipeServer _server;
    private readonly AgentRpcOptions _options;
    private readonly CancellationTokenSource _cts = new();

    private ulong? _deviceAddress;
    private bool _userDisconnected;
    private int? _ancMode;     // 0 normal / 1 cancellation / 2 awareness
    private int? _ancLevel;    // 0 normal / 1 comfort / 2 ultra / 3 dynamic
    private (int? Global, int? Left, int? Right, int? Case, bool? Charging)? _battery;
    private bool? _lowLatency;
    private bool _rawSubscribed;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    private Task? _heartbeatLoop;
    private Task? _selfHealLoop;
    private Timer? _orphanTimer;
    private Timer? _batteryPollTimer;
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ProtocolVersion => "1";

    public AgentRpc(ISppLink link, NamedPipeServer server, AgentRpcOptions options)
    {
        _link = link;
        _server = server;
        _options = options;

        _server.RequestReceived += OnRequest;
        _server.ClientConnected += OnClientConnected;
        _server.ClientDisconnected += OnClientDisconnected;

        _link.LinkLost += OnLinkLost;
        _link.RawFrame += OnRawFrame;
        _link.OnNotify(Cmd.BatteryNotify, OnBatteryFrame);
        _link.OnNotify(Cmd.AncRead, OnAncFrame);       // 写确认帧 2B2A
        _link.OnNotify(Cmd.AncChangeEvent, OnAncChangeEventAsync); // 耳机按键事件 2B03（param1=[level,mode]）
    }

    /// <summary>优雅退出信号（Program await 它后返回退出码 0）。</summary>
    public Task ShutdownSignal => _shutdownTcs.Task;

    public void Start()
    {
        _server.Start();
        _heartbeatLoop = Task.Run(() => HeartbeatLoop(_cts.Token));
    }

    // ---- 请求分发 ----

    private void OnRequest(IpcRequest request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await DispatchAsync(request);
                await _server.SendAsync(IpcResponse.Success(request.Id, result));
            }
            catch (Exception ex)
            {
                await _server.SendAsync(IpcResponse.Fail(request.Id, ex.GetType().Name, ex.Message));
            }
        });
    }

    private async Task<object?> DispatchAsync(IpcRequest request)
    {
        switch (request.Method)
        {
            case "hello":
                return new { version = ProtocolVersion };
            case "ping":
                return new { t = DateTime.UtcNow.ToString("o") };
            case "shutdown":
                await _server.SendAsync(IpcResponse.Success(request.Id, new { ok = true }));
                await ShutdownAsync();
                return null;

            case "connect":
                // 幂等：链路已在（如主程序重发 connect）直接回快照，避免重复建链破坏收包循环
                if (_link.Started)
                {
                    return StateSnapshot();
                }
                _userDisconnected = false; // 用户主动恢复连接
                await ConnectAsync(ReadAddress(request.Params));
                return StateSnapshot();

            case "disconnect":
                // 用户主动断开：停链路并阻止 L3 自愈拉回（直到下次 connect/设备回归）
                _userDisconnected = true;
                await _link.StopAsync();
                PublishConnection("disconnected");
                return StateSnapshot();

            case "getState":
                return StateSnapshot();

            case "setAncMode":
                return await SetAncModeAsync(request.Params);

            case "setEq":
                return await SetEqAsync(request.Params);

            case "setLowLatency":
                return await SetLowLatencyAsync(request.Params);

            case "debugSendRaw":
            {
                var hex = ReadString(request.Params, "hex") ?? "";
                var bytes = Convert.FromHexString(hex);
                var pkg = HuaweiSppPackage.FromBytes(bytes);
                await _link.SendPackageAsync(pkg, 5000);
                return new { sent = true };
            }

            case "debugSubscribeRaw":
                _rawSubscribed = true;
                return new { subscribed = true };

            case "debugUnsubscribeRaw":
                _rawSubscribed = false;
                return new { subscribed = false };

            default:
                throw new NotSupportedException($"未知方法 {request.Method}");
        }
    }

    // ---- 连接与自愈 ----

    private async Task ConnectAsync(ulong? address)
    {
        var addr = address ?? _deviceAddress
            ?? throw new ArgumentException("未提供 deviceAddress，且无既往连接记录");
        _deviceAddress = addr;
        PublishConnection("connecting");

        try
        {
            await _link.StartAsync(addr, _options.RfcommChannel);
            PublishConnection("connected");
            StartBatteryPoll();
            _selfHealLoop ??= Task.Run(() => SelfHealLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            PublishConnection("failed", ex.Message);
            throw;
        }
    }

    /// <summary>L3 自愈：链路断开由 Agent 内部指数退避重连，进程不重启（§3.1-D）。</summary>
    private async Task SelfHealLoop(CancellationToken ct)
    {
        var backoff = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct);
                // 用户主动断开（disconnect）期间不自愈，否则断开会被立刻拉回
                if (_userDisconnected || _link.Started || _deviceAddress is null) continue;
                PublishConnection("reconnecting");
                try
                {
                    await _link.StartAsync(_deviceAddress.Value, _options.RfcommChannel);
                    PublishConnection("connected");
                    backoff = 1000;
                }
                catch
                {
                    backoff = Math.Min(backoff * 2, 30000);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnLinkLost()
    {
        PublishConnection("reconnecting");
    }

    // ---- 方法实现 ----

    private async Task<object> SetAncModeAsync(JsonElement? parameters)
    {
        var modeStr = ReadString(parameters, "mode")
            ?? throw new ArgumentException("缺少 mode");
        AncMode mode;
        AncLevel? level = null;

        // IPC 五态 → 华为 (mode, level)；映射见《软件实现计划》K10/§4
        switch (modeStr.ToLowerInvariant())
        {
            case "off":
                mode = AncMode.Normal; break;
            case "transparency":
                mode = AncMode.Awareness; break;
            case "ultra":
                (mode, level) = (AncMode.Cancellation, AncLevel.Ultra); break;
            case "general":
                (mode, level) = (AncMode.Cancellation, AncLevel.Normal); break;
            case "cozy":
                (mode, level) = (AncMode.Cancellation, AncLevel.Comfort); break;
            case "dynamic":
                (mode, level) = (AncMode.Cancellation, AncLevel.Dynamic); break;
            default:
                throw new ArgumentException($"未知模式 {modeStr}");
        }

        if (ReadString(parameters, "level") is { } levelStr)
        {
            level = ParseEnum<AncLevel>(levelStr);
        }

        HuaweiSppPackage pkg = mode switch
        {
            _ when level.HasValue && mode == AncMode.Cancellation
                => HuaweiCommands.AncSetLevelRequest(AncMode.Cancellation, level.Value),
            _ when level.HasValue && mode == AncMode.Awareness
                => HuaweiCommands.AncSetLevelRequest(AncMode.Awareness, level.Value),
            _ => HuaweiCommands.AncSetModeRequest(mode),
        };

        // 耳机以 2B2A 帧确认（param1=[level, mode]），超时也认为写已发出（M1 实测行为）
        var resp = await _link.SendPackageAsync(pkg, 5000, Cmd.AncRead);
        if (resp is not null && HuaweiCommands.ParseAnc(resp) is { } state)
        {
            _ancLevel = (int)state.Level;
            _ancMode = (int)state.Mode;
        }
        else
        {
            _ancMode = (int)mode;
            if (level.HasValue) _ancLevel = (int)level.Value;
        }
        PublishState();
        return StateSnapshot();
    }

    private async Task<object> SetEqAsync(JsonElement? parameters)
    {
        var preset = ReadString(parameters, "preset")
            ?? throw new ArgumentException("缺少 preset");
        var entry = HuaweiCommands.EqPresets.FirstOrDefault(kv => kv.Value == preset);
        if (entry.Key == 0)
        {
            throw new ArgumentException($"未知预设 {preset}");
        }
        await _link.SendPackageAsync(HuaweiCommands.EqSetPresetRequest(entry.Key), 5000, Cmd.EqRead);
        return new { preset };
    }

    private async Task<object> SetLowLatencyAsync(JsonElement? parameters)
    {
        var enabled = ReadBool(parameters, "enabled");
        await _link.SendPackageAsync(HuaweiCommands.LowLatencyWriteRequest(enabled));
        _lowLatency = enabled;
        PublishState();
        return StateSnapshot();
    }

    // ---- 链路帧处理 → 状态 + 推送 ----

    private Task OnBatteryFrame(HuaweiSppPackage pkg)
    {
        _battery = HuaweiCommands.ParseBattery(pkg);
        PublishState();
        return Task.CompletedTask;
    }

    private Task OnAncFrame(HuaweiSppPackage pkg)
    {
        if (HuaweiCommands.ParseAnc(pkg) is { } state)
        {
            _ancMode = (int)state.Mode;
            _ancLevel = (int)state.Level;
            PublishState();
        }
        return Task.CompletedTask;
    }

    private async Task OnAncChangeEventAsync(HuaweiSppPackage pkg)
    {
        // 协议笔记 5.1-4：OpenFreebuds 收到 2B03 按键事件后回 2B2A param1="" 确认，不回可能影响后续事件推送
        try
        {
            await _link.SendPackageAsync(
                HuaweiSppPackage.ChangeRequestNoWait(Cmd.AncRead, (1, [])));
        }
        catch
        {
            // 链路断开场景忽略（L3 自愈接管）
        }
        if (HuaweiCommands.ParseAnc(pkg) is { } state)
        {
            _ancMode = (int)state.Mode;
            _ancLevel = (int)state.Level;
            PublishState();
        }
    }

    // ---- 电量轮询兜底（协议笔记 5.1-1：0127 仅连接时推一次，发 0108 可触发再推） ----

    private void StartBatteryPoll()
    {
        _batteryPollTimer?.Dispose();
        _batteryPollTimer = new Timer(async _ =>
        {
            try
            {
                if (!_link.Started) return;
                // 读请求无直接响应，但会触发耳机再推一帧 0127；等 2s 内的电量帧即可
                await _link.SendPackageAsync(HuaweiCommands.BatteryReadRequest(), 2000, Cmd.BatteryNotify);
            }
            catch
            {
                // 链路断开/冲突：忽略，下轮再试
            }
        }, null, _options.BatteryPollMs, _options.BatteryPollMs);
    }

    private void OnRawFrame(string dir, byte[] bytes)
    {
        if (!_rawSubscribed) return;
        _ = _server.SendAsync(new IpcEvent("rawPacket",
            JsonSerializer.SerializeToElement(new { dir, hex = Convert.ToHexString(bytes) }, IpcJson.Options)));
    }

    // ---- 状态/事件 ----

    private object StateSnapshot() => new
    {
        connection = _link.Started ? "connected" : "disconnected",
        deviceAddress = _deviceAddress?.ToString("X12"),
        battery = _battery is null ? null : new
        {
            left = _battery.Value.Left,
            right = _battery.Value.Right,
            caseB = _battery.Value.Case,
            global = _battery.Value.Global,
            charging = _battery.Value.Charging,
        },
        anc = (_ancMode is null && _ancLevel is null) ? null : new
        {
            mode = _ancMode?.ToString(),
            level = _ancLevel?.ToString(),
        },
        lowLatency = _lowLatency,
    };

    private void PublishState()
    {
        _ = _server.SendAsync(new IpcEvent("stateChanged",
            JsonSerializer.SerializeToElement(StateSnapshot(), IpcJson.Options)));
    }

    private void PublishConnection(string status, string? error = null)
    {
        _ = _server.SendAsync(new IpcEvent("connectionChanged",
            JsonSerializer.SerializeToElement(new { status, error }, IpcJson.Options)));
    }

    // ---- heartbeat（L2：静默期才发，活跃期静默） ----

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatIntervalMs, ct);
                if ((DateTime.UtcNow - _lastActivity).TotalMilliseconds < _options.HeartbeatIntervalMs)
                {
                    continue; // 近期有帧，静默
                }
                _lastHeartbeat = DateTime.UtcNow;
                _lastActivity = _lastHeartbeat;
                await _server.SendAsync(new IpcEvent("heartbeat", null));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Touch() => _lastActivity = DateTime.UtcNow;

    // ---- 孤儿退出（§3.1-B：主程序死亡后 30s 无新客户端 → 自行退出） ----

    private void OnClientConnected()
    {
        Touch();
        _orphanTimer?.Dispose();
        _orphanTimer = null;
    }

    private void OnClientDisconnected()
    {
        _orphanTimer?.Dispose();
        _orphanTimer = new Timer(_ =>
        {
            if (!_server.HasClient)
            {
                _ = ShutdownAsync();
            }
        }, null, _options.OrphanGraceMs, Timeout.Infinite);
    }

    private async Task ShutdownAsync()
    {
        _cts.Cancel();
        _batteryPollTimer?.Dispose();
        await _link.StopAsync();
        _shutdownTcs.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _orphanTimer?.Dispose();
        _batteryPollTimer?.Dispose();
        await _link.StopAsync();
        await _server.DisposeAsync();
        _cts.Dispose();
    }

    // ---- 参数读取辅助 ----

    private static string? ReadString(JsonElement? parameters, string name) =>
        parameters is JsonElement p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool ReadBool(JsonElement? parameters, string name) =>
        parameters is JsonElement p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static ulong? ReadAddress(JsonElement? parameters)
    {
        var text = ReadString(parameters, "deviceAddress");
        return text is null
            ? null
            : ulong.Parse(text.Replace(":", ""), System.Globalization.NumberStyles.HexNumber);
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var v)
            ? v
            : throw new ArgumentException($"无效枚举值 {value}");
}

public sealed record AgentRpcOptions
{
    public int HeartbeatIntervalMs { get; init; } = 30_000;
    public int OrphanGraceMs { get; init; } = 30_000;
    public int RfcommChannel { get; init; } = 1;
    /// <summary>电量轮询兜底周期（发 0108 触发 0127 推送；协议笔记 5.1-1）。</summary>
    public int BatteryPollMs { get; init; } = 60_000;
}
