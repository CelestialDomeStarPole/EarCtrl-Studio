using System.Text.Json;
using EarCtrl.Core.Profiles;
using EarCtrl.Core.Supervision;
using EarCtrl.Ipc.Protocol;

namespace EarCtrl.Core.StateMachine;

/// <summary>降噪五态（IPC setAncMode 契约值；K11 降噪页 / 托盘共用）。</summary>
public enum AncFiveState
{
    Unknown,
    Off,
    Ultra,        // 深度
    General,      // 均衡
    Cozy,         // 轻度
    Transparency, // 透传
    Dynamic,      // 动态
}

/// <summary>
/// 华为 (mode, level) ↔ 五态映射（协议笔记 5.1）：
/// off→[00,00]、ultra→[01,02]、general→[01,00]、cozy→[01,01]、transparency→[02,FF]、dynamic→[01,03]。
/// 展示规则：mode=0 显示「关闭」，档位仅作缓存（M2 实测：关闭确认帧带 level=3）。
/// </summary>
public static class AncFiveStateMapper
{
    public static AncFiveState FromHuawei(int? mode, int? level) => mode switch
    {
        null => AncFiveState.Unknown,
        0 => AncFiveState.Off,
        2 => AncFiveState.Transparency,
        1 => level switch
        {
            2 => AncFiveState.Ultra,
            1 => AncFiveState.Cozy,
            3 => AncFiveState.Dynamic,
            _ => AncFiveState.General,
        },
        _ => AncFiveState.Unknown,
    };

    public static string ToIpcMode(AncFiveState state) => state switch
    {
        AncFiveState.Off => "off",
        AncFiveState.Ultra => "ultra",
        AncFiveState.General => "general",
        AncFiveState.Cozy => "cozy",
        AncFiveState.Transparency => "transparency",
        AncFiveState.Dynamic => "dynamic",
        _ => throw new ArgumentException("Unknown 状态不能下发"),
    };
}

/// <summary>单设备运行时快照（Agent IPC 事件 + Supervisor 状态合并）。</summary>
public sealed class DeviceRuntimeState
{
    public AgentState SupervisorState { get; internal set; } = AgentState.Idle;
    /// <summary>Agent connectionChanged：connected/connecting/disconnected/reconnecting/failed。</summary>
    public string? LinkStatus { get; internal set; }
    public string? LinkError { get; internal set; }

    public int? BatteryLeft { get; internal set; }
    public int? BatteryRight { get; internal set; }
    public int? BatteryCase { get; internal set; }
    public int? BatteryGlobal { get; internal set; }
    public bool? Charging { get; internal set; }
    /// <summary>充电盒是否正在充电（电量帧 param3 第三字节 &gt;0；官方 BatteryResult chargingState[2]）。</summary>
    public bool? CaseCharging { get; internal set; }

    /// <summary>电压（V）。协议上报才有值，否则 null（UI 显示 --）。头戴式复用 Left 系字段。</summary>
    public double? VoltageLeft { get; internal set; }
    public double? VoltageRight { get; internal set; }
    public double? VoltageCase { get; internal set; }
    /// <summary>温度（°C）。</summary>
    public double? TempLeft { get; internal set; }
    public double? TempRight { get; internal set; }
    /// <summary>该耳是否在充电盒内（0x01 开盖在仓 / 0x02 关盖在仓均为 true；null = 未收到电量帧）。</summary>
    public bool? InCaseLeft { get; internal set; }
    public bool? InCaseRight { get; internal set; }
    /// <summary>在仓且盒盖已合（0x02；此状态下耳机侧电量上报为 0 占位）。</summary>
    public bool? LidClosedLeft { get; internal set; }
    public bool? LidClosedRight { get; internal set; }

    public AncFiveState Anc { get; internal set; } = AncFiveState.Unknown;
    /// <summary>华为原始模式字节（0=关闭 1=降噪 2=透传；AncPage 三按钮选中态）。</summary>
    public int? AncModeRaw { get; internal set; }
    /// <summary>华为原始档位字节（0=均衡 1=轻度 2=深度 3=动态；AncPage 等级下拉选中项）。</summary>
    public int? AncLevelRaw { get; internal set; }
    public bool? LowLatency { get; internal set; }
    /// <summary>防风噪开关（华为 2B B4 子功能总线回报；null = 未取得状态）。全局状态：所有非动态降噪档共用。</summary>
    public bool? WindNoise { get; internal set; }

    /// <summary>可下发控制命令（Agent 就绪且协议链路已连）。</summary>
    public bool IsControllable => SupervisorState == AgentState.Ready && LinkStatus == "connected";
}

/// <summary>
/// K10：全局当前设备 + 各设备运行时状态（Core 单例）。
/// 侧边栏点选设备 = SetCurrent；所有功能页跟随 Current 作用于该设备。
/// </summary>
public sealed class DeviceContext
{
    public static DeviceContext Instance { get; } = new();
    private DeviceContext() { }

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceRuntimeState> _states = new();
    private readonly List<DeviceProfile> _devices = [];

    /// <summary>当前设备切换。</summary>
    public event Action? CurrentChanged;
    /// <summary>某设备运行时状态变化（brand）。</summary>
    public event Action<string>? StateChanged;
    /// <summary>已添加设备列表变化（侧边栏重建）。</summary>
    public event Action? DevicesChanged;

    public DeviceProfile? Current { get; private set; }

    public IReadOnlyList<DeviceProfile> Devices
    {
        get { lock (_gate) return _devices.ToList(); }
    }

    public DeviceRuntimeState GetState(string brand)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(brand, out var state))
            {
                state = new DeviceRuntimeState();
                _states[brand] = state;
            }
            return state;
        }
    }

    /// <summary>启动时/添加设备时整表重建（首启动 Current 取第一个）。</summary>
    public void SetDevices(IEnumerable<DeviceProfile> devices)
    {
        lock (_gate)
        {
            _devices.Clear();
            _devices.AddRange(devices);
            foreach (var d in _devices)
            {
                _states.TryAdd(d.Brand, new DeviceRuntimeState());
            }
            if (Current is null || _devices.All(d => d.Brand != Current.Brand))
            {
                Current = _devices.FirstOrDefault();
            }
        }
        DevicesChanged?.Invoke();
        CurrentChanged?.Invoke();
    }

    public void AddDevice(DeviceProfile profile)
    {
        lock (_gate)
        {
            if (_devices.Any(d => d.Brand == profile.Brand)) return;
            _devices.Add(profile);
            _states.TryAdd(profile.Brand, new DeviceRuntimeState());
            Current ??= profile;
        }
        DevicesChanged?.Invoke();
    }

    public void SetCurrent(string brand)
    {
        lock (_gate)
        {
            var target = _devices.FirstOrDefault(d => d.Brand == brand);
            if (target is null || Current?.Brand == brand) return;
            Current = target;
        }
        CurrentChanged?.Invoke();
    }

    public void UpdateSupervisorState(string brand, AgentState state)
    {
        GetState(brand).SupervisorState = state;
        StateChanged?.Invoke(brand);
    }

    /// <summary>Agent IPC 事件 → 快照（stateChanged/connectionChanged；其余事件不触发刷新）。</summary>
    public void UpdateAgentEvent(string brand, IpcEvent e)
    {
        var state = GetState(brand);
        switch (e.Event)
        {
            case "connectionChanged":
                state.LinkStatus = Json.ReadString(e.Payload, "status") ?? state.LinkStatus;
                state.LinkError = Json.ReadString(e.Payload, "error");
                break;

            case "stateChanged":
                state.LinkStatus = Json.ReadString(e.Payload, "connection") ?? state.LinkStatus;
                if (Json.GetObject(e.Payload, "battery") is { } battery)
                {
                    state.BatteryLeft = Json.ReadInt(battery, "left") ?? state.BatteryLeft;
                    state.BatteryRight = Json.ReadInt(battery, "right") ?? state.BatteryRight;
                    state.BatteryCase = Json.ReadInt(battery, "caseB") ?? state.BatteryCase;
                    state.BatteryGlobal = Json.ReadInt(battery, "global") ?? state.BatteryGlobal;
                    state.Charging = Json.ReadBool(battery, "charging") ?? state.Charging;
                    state.CaseCharging = Json.ReadBool(battery, "caseCharging") ?? state.CaseCharging;
                    state.VoltageLeft = Json.ReadDouble(battery, "voltageLeft") ?? state.VoltageLeft;
                    state.VoltageRight = Json.ReadDouble(battery, "voltageRight") ?? state.VoltageRight;
                    state.VoltageCase = Json.ReadDouble(battery, "voltageCase") ?? state.VoltageCase;
                    state.TempLeft = Json.ReadDouble(battery, "tempLeft") ?? state.TempLeft;
                    state.TempRight = Json.ReadDouble(battery, "tempRight") ?? state.TempRight;
                    state.InCaseLeft = Json.ReadBool(battery, "inCaseLeft") ?? state.InCaseLeft;
                    state.InCaseRight = Json.ReadBool(battery, "inCaseRight") ?? state.InCaseRight;
                    state.LidClosedLeft = Json.ReadBool(battery, "lidClosedLeft") ?? state.LidClosedLeft;
                    state.LidClosedRight = Json.ReadBool(battery, "lidClosedRight") ?? state.LidClosedRight;
                }
                if (Json.GetObject(e.Payload, "anc") is { } anc)
                {
                    state.Anc = AncFiveStateMapper.FromHuawei(Json.ReadInt(anc, "mode"), Json.ReadInt(anc, "level"));
                    state.AncModeRaw = Json.ReadInt(anc, "mode") ?? state.AncModeRaw;
                    state.AncLevelRaw = Json.ReadInt(anc, "level") ?? state.AncLevelRaw;
                }
                state.LowLatency = Json.ReadBool(e.Payload, "lowLatency") ?? state.LowLatency;
                state.WindNoise = Json.ReadBool(e.Payload, "windNoise") ?? state.WindNoise;
                break;

            default:
                return; // heartbeat/rawPacket/log
        }
        StateChanged?.Invoke(brand);
    }
}

/// <summary>JsonElement? 快照字段读取辅助（Agent 帧字段全为 camelCase、忽略 null）。</summary>
internal static class Json
{
    public static string? ReadString(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e &&
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int? ReadInt(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e ||
            !e.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt32(),
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }

    public static double? ReadDouble(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e ||
            !e.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => null,
        };
    }

    public static bool? ReadBool(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e ||
            !e.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    public static JsonElement? GetObject(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e &&
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;
}
