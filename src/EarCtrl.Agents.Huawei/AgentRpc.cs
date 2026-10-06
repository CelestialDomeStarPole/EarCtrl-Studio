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
    // 0127/0108 param_5 = [左状态字节, 右状态字节]（真机实测定论）：
    // 0x00=佩戴(出盒)、0x01=开盖在仓、0x02=关盖在仓
    private byte? _p5Left;
    private byte? _p5Right;
    private bool? _lowLatency;
    private bool _rawSubscribed;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    private Task? _heartbeatLoop;
    private Task? _selfHealLoop;
    private Timer? _orphanTimer;
    /// <summary>链路操作互斥：connect/disconnect/L3 自愈串行化，杜绝「断开被并发重连抢回」竞态。</summary>
    private readonly SemaphoreSlim _linkGate = new(1, 1);
    private int _selfHealFailures;
    private bool _selfHealSurrendered;
    private bool _lastObservedDevicePresent = true;
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
        // 0108 查询的回帧与 0127 推送帧同构（monitor3 实测 cmd=0108），必须同样挂处理，否则轮询数据全被丢弃
        _link.OnNotify(Cmd.BatteryRead, OnBatteryFrame);
        _link.OnNotify(Cmd.KeepAlive, _ => Task.CompletedTask);    // 2B70 回包（param1=[01]）吞掉，避免未注册告警
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
                // 用户主动恢复连接；内建快速重试（旧 RFCOMM 会话释放需要 1~3s，立即重连常失败）
                _userDisconnected = false;
                try
                {
                    await ConnectWithRetryAsync(ReadAddress(request.Params), attempts: 3, delayMs: 500);
                }
                catch (Exception ex)
                {
                    PublishConnection("failed", ex.Message);
                    throw;
                }
                return StateSnapshot();

            case "disconnect":
                // 用户主动断开：停链路并阻止 L3 自愈拉回（直到下次 connect/设备回归）。
                // 拿链路锁：若自愈正在重建 SPP，等它完成后真正停掉（保证断开必然生效）
                _userDisconnected = true;
                await _linkGate.WaitAsync(_cts.Token);
                try
                {
                    await _link.StopAsync();
                }
                finally
                {
                    _linkGate.Release();
                }
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

    /// <summary>connect（IPC）带快速重试：旧 RFCOMM 会话释放期间建链会失败，几百 ms 后重试通常即可成功。</summary>
    private async Task ConnectWithRetryAsync(ulong? address, int attempts, int delayMs)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ConnectAsync(address);
                return;
            }
            catch (Exception)
            {
                if (attempt >= attempts) throw;
                PublishConnection("reconnecting");
                await Task.Delay(delayMs, _cts.Token);
            }
        }
    }

    private async Task ConnectAsync(ulong? address)
    {
        var addr = address ?? _deviceAddress
            ?? throw new ArgumentException("未提供 deviceAddress，且无既往连接记录");
        _deviceAddress = addr;
        PublishConnection("connecting");

        await _linkGate.WaitAsync(_cts.Token);
        try
        {
            // 幂等：链路已在（主程序重发 connect / 自愈刚连上）直接报已连接
            if (!_link.Started)
            {
                await _link.StartAsync(addr, _options.RfcommChannel);
                OnSessionEstablished();
                _selfHealLoop ??= Task.Run(() => SelfHealLoop(_cts.Token));
            }
            PublishConnection("connected");
            _selfHealSurrendered = false;
            _selfHealFailures = 0;
        }
        finally
        {
            _linkGate.Release();
        }
    }

    /// <summary>
    /// L3 自愈：链路意外断开时指数退避重连（500ms 起步 ×2，10s 封顶），进程不重启（§3.1-D）。
    /// - 设备系统层已断开 → 绝不重连（出站 RFCOMM 会把设备拉回），并发布 disconnected 让 UI 收敛；
    /// - 连续 3 次 SPP 建链失败 → 放弃自愈（出站尝试会把 ACL 拉回造成「设备在线」假象，无限重试会让 UI 永远「重连中」）；
    /// - 设备离开（Disconnected→Connected 边沿）后回来 → 重置失败计数，给新一轮重试机会；
    /// - connect 指令成功会无条件重置放弃状态。
    /// </summary>
    private async Task SelfHealLoop(CancellationToken ct)
    {
        var backoff = 500;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(backoff, ct);
                if (_userDisconnected || _deviceAddress is null) continue;

                // 设备系统层连接状态（锁外查询）：不在线则绝不发起出站连接
                var deviceConnected = await IsDeviceConnectedAsync(_deviceAddress.Value);
                var deviceReappeared = deviceConnected && !_lastObservedDevicePresent;
                _lastObservedDevicePresent = deviceConnected;

                if (!deviceConnected)
                {
                    if (!_selfHealSurrendered)
                    {
                        // 状态收敛：设备不在 = 未连接（一次性发布，避免 UI 停在「重连中」）
                        _selfHealSurrendered = true;
                        PublishConnection("disconnected");
                    }
                    continue;
                }

                if (deviceReappeared && _selfHealSurrendered)
                {
                    _selfHealSurrendered = false;
                    _selfHealFailures = 0;
                }

                if (_selfHealSurrendered) continue;

                await _linkGate.WaitAsync(ct);
                try
                {
                    if (_userDisconnected || _link.Started) continue;
                    PublishConnection("reconnecting");
                    try
                    {
                        await _link.StartAsync(_deviceAddress.Value, _options.RfcommChannel);
                        PublishConnection("connected");
                        OnSessionEstablished();
                        _selfHealSurrendered = false;
                        _selfHealFailures = 0;
                        backoff = 500;
                    }
                    catch
                    {
                        _selfHealFailures++;
                        backoff = Math.Min(backoff * 2, 10000);
                        if (_selfHealFailures >= 3)
                        {
                            _selfHealSurrendered = true;
                            PublishConnection("disconnected", "SPP 连续建立失败，已停止自动重连");
                        }
                    }
                }
                finally
                {
                    _linkGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>设备在系统蓝牙层面是否已连接（A2DP/HFP 任一 profile 活跃）。查询失败时保守返回 true。</summary>
    private static async Task<bool> IsDeviceConnectedAsync(ulong address)
    {
        try
        {
            var device = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(address);
            return device is null ||
                   device.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected;
        }
        catch
        {
            return true; // 查询失败不阻塞自愈（保守尝试）
        }
    }

    private void OnLinkLost()
    {
        // 断链即停轮询：链路不在时查询无意义（重连成功后 OnSessionEstablished 会重建）
        _statePollTimer?.Dispose();
        _statePollTimer = null;
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
        // 忠实上报原始电量（含关盖在仓时的 0 占位）——占位场景的显示处理在 App 端 UI 规则完成
        _battery = HuaweiCommands.ParseBattery(pkg);
        var wear = pkg.FindParam(5);
        _p5Left = wear.Length >= 1 ? (byte?)wear[0] : null;   // param_5[0] = 左耳
        _p5Right = wear.Length >= 2 ? (byte?)wear[1] : null;  // param_5[1] = 右耳
        PublishStateIfChanged();
        return Task.CompletedTask;
    }

    private Task OnAncFrame(HuaweiSppPackage pkg)
    {
        if (HuaweiCommands.ParseAnc(pkg) is { } state)
        {
            _ancMode = (int)state.Mode;
            _ancLevel = (int)state.Level;
            PublishStateIfChanged();
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
            PublishStateIfChanged();
        }
        // 2B03 意味着耳机侧发生了状态变化——立即补发一次 0108，加速 param5（在盒/佩戴）刷新
        try
        {
            if (_link.Started && !_userDisconnected)
            {
                await _link.SendPackageAsync(HuaweiCommands.BatteryReadNoWaitRequest());
            }
        }
        catch { /* 链路瞬断：L3 自愈接管 */ }
    }

    // ---- 状态轮询（真机实验定论，协议笔记 §7.6）：T0026 不做事件推送，且 0108 查询必须以
    //      2B70 使能帧开路才回帧（polltest 实测：纯 0108 轮询 37 发 0 回；2B70+0108 成对则每问必答）。
    //      1.2s 一对「2B70 → 0108」，回帧 param5 携带在仓/佩戴状态，发布前做变化去抖 ----

    private Timer? _statePollTimer;
    private string? _lastPublishedState;

    /// <summary>每次 SPP 建链成功后调用（首连/自愈重连均走此路径）。</summary>
    private void OnSessionEstablished()
    {
        _ = RunSessionInitAsync(_cts.Token);
        StartStatePoll();
    }

    private async Task RunSessionInitAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(200, ct); // 等建链完全稳定
            // 使能查询通道后立即拿一帧全量状态
            await _link.SendPackageAsync(HuaweiSppPackage.ChangeRequestNoWait(Cmd.KeepAlive, (1, [0xCB])));
            await Task.Delay(150, ct);
            if (!_link.Started) return;
            await _link.SendPackageAsync(HuaweiCommands.BatteryReadNoWaitRequest());
        }
        catch (OperationCanceledException) { }
        catch { /* 初始化失败不致命：轮询下一拍兜底 */ }
    }

    private void StartStatePoll()
    {
        if (_statePollTimer != null) return;
        _statePollTimer = new Timer(async _ =>
        {
            try
            {
                if (!_link.Started || _userDisconnected) return;
                // 先发 2B70 使能帧，再发 0108 查询（成对，缺一不可）
                await _link.SendPackageAsync(HuaweiSppPackage.ChangeRequestNoWait(Cmd.KeepAlive, (1, [0xCB])));
                await Task.Delay(200);
                if (!_link.Started || _userDisconnected) return;
                await _link.SendPackageAsync(HuaweiCommands.BatteryReadNoWaitRequest());
            }
            catch { /* 链路瞬断：L3 自愈接管 */ }
        }, null, _options.StatePollMs, _options.StatePollMs);
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
            // param_5 = [左状态, 右状态]：0x00=佩戴(出盒)，0x01=开盖在仓，0x02=关盖在仓（真机实测定论）
            inCaseLeft = _p5Left.HasValue && (_p5Left.Value & 0x03) != 0,
            inCaseRight = _p5Right.HasValue && (_p5Right.Value & 0x03) != 0,
            lidClosedLeft = _p5Left == 0x02,
            lidClosedRight = _p5Right == 0x02,
        },
        wearingLeft = _p5Left == 0x00,
        wearingRight = _p5Right == 0x00,
        anc = (_ancMode is null && _ancLevel is null) ? null : new
        {
            mode = _ancMode?.ToString(),
            level = _ancLevel?.ToString(),
        },
        lowLatency = _lowLatency,
    };

    private void PublishState()
    {
        _lastPublishedState = JsonSerializer.Serialize(StateSnapshot(), IpcJson.Options);
        _ = _server.SendAsync(new IpcEvent("stateChanged",
            JsonSerializer.SerializeToElement(StateSnapshot(), IpcJson.Options)));
    }

    /// <summary>状态去抖：快照与上次发布完全一致时不发 stateChanged（1s 轮询下避免无效刷 UI）。</summary>
    private void PublishStateIfChanged()
    {
        var json = JsonSerializer.Serialize(StateSnapshot(), IpcJson.Options);
        if (json == _lastPublishedState) return;
        _lastPublishedState = json;
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
        // 先放行退出信号再清理：清理若挂起（WinRT 取消不响应），进程仍能走 DisposeAsync 退出，
        // 不会留下「_cts 已取消但 IPC 服务还开着」的僵尸（connect 会全部立即失败 "A task was canceled."）
        _shutdownTcs.TrySetResult();
        await _link.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _orphanTimer?.Dispose();
        _statePollTimer?.Dispose();
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
    /// <summary>状态轮询周期：「2B70 使能 → 0108 查询」成对发送（回帧含电量 + param5 在仓/佩戴），1s 保证及时性。</summary>
    public int StatePollMs { get; init; } = 1_000;
}
