// SPDX-License-Identifier: GPL-3.0-only
// M1 PoC 控制台：真机验证 连接 → 读电量 → 读/切降噪五态。
// 用法：
//   EarCtrl.Agent.Huawei --address AA:BB:CC:DD:EE:FF battery
//   EarCtrl.Agent.Huawei --address AA:BB:CC:DD:EE:FF anc                       # 读当前模式/档位
//   EarCtrl.Agent.Huawei --address AA:BB:CC:DD:EE:FF anc set cancellation ultra
//   EarCtrl.Agent.Huawei --address AA:BB:CC:DD:EE:FF eq                        # 读 EQ 预设
//   EarCtrl.Agent.Huawei --address AA:BB:CC:DD:EE:FF eq set hardbass
//   EarCtrl.Agent.Huawei --address AA:BB:CC:DD:EE:FF lowlatency                # 读
//   EarCtrl.Agent.Huawei --address AA:BB:CC:DD:EE:FF lowlatency on|off
//   --verbose 开启逐帧 TX/RX 日志

using System.Globalization;
using EarCtrl.Agents.Huawei;
using EarCtrl.Agents.Huawei.Commands;
using EarCtrl.Agents.Huawei.Lpv2;
using EarCtrl.Ipc.NamedPipe;

var options = AgentOptions.Parse(args);

// 服务模式：主程序以 --pipe 拉起，进入 IPC 服务；否则进入 PoC CLI
if (options.PipeName is not null)
{
    return await RunServiceModeAsync(options);
}

if (options.Command is null || (options.Address is null && options.Command is not ("list" or "selftest")))
{
    PrintUsage();
    return 2;
}

if (options.Command == "selftest")
{
    return await RunSelfTestAsync(options);
}

if (options.Command == "list")
{
    var paired = Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
            Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true))
        .GetAwaiter().GetResult();
    foreach (var d in paired)
    {
        var dev = Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(d.Id).GetAwaiter().GetResult();
        Console.Out.WriteLine(
            $"{dev.Name,-32} {dev.BluetoothAddress:X12} connected={dev.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected}");
    }
    return 0;
}

var address = ParseAddress(options.Address);

var client = new HuaweiSppClient();
if (options.Verbose)
{
    client.LogDebug += m => Console.Out.WriteLine($"[dbg] {m}");
    client.LogWarn += m => Console.Out.WriteLine($"[warn] {m}");
}

// 电量/ANC 变化推送 → 实时打印
client.OnNotify(Cmd.BatteryNotify, pkg =>
{
    Console.Out.WriteLine($"[push] 电量: {FmtBattery(HuaweiCommands.ParseBattery(pkg))}");
    return Task.CompletedTask;
});
client.OnNotify(Cmd.AncChangeEvent, pkg =>
{
    Console.Out.WriteLine("[push] 耳机上按键切换了降噪模式，回读中...");
    return SendAndPrintAsync(client, HuaweiCommands.AncReadRequest(),
        p => HuaweiCommands.ParseAnc(p) is { } v ? $"模式={v.Mode} 档位={v.Level}" : null);
});

Console.Out.WriteLine($"连接 {options.Address} (SPP/RFCOMM)...");
await client.StartAsync(address);
Console.Out.WriteLine("已连接。");
// 协议要点（M1 实机验证）：耳机不发读响应，状态通过 ①连接即推 0127 ②2B04 写后回 2B2A 确认帧（param1=[档位,模式]）

switch (options.Command)
{
    case "battery":
        await SendAndPrintAsync(client, HuaweiCommands.BatteryReadRequest(),
            p => FmtBattery(HuaweiCommands.ParseBattery(p)), Cmd.BatteryNotify);
        break;

    case "anc":
        if (options.Arguments is ["set", var modeStr, ..])
        {
            var mode = ParseEnum<AncMode>(modeStr);
            var pkg = mode == AncMode.Cancellation && options.Arguments.Length == 3
                ? HuaweiCommands.AncSetLevelRequest(AncMode.Cancellation, ParseEnum<AncLevel>(options.Arguments[2]))
                : HuaweiCommands.AncSetModeRequest(mode);
            // 耳机以 2B2A 帧确认写入（param1=[档位,模式]）
            var resp = await client.SendPackageAsync(pkg, 5000, Cmd.AncRead);
            var state = resp is null ? null : HuaweiCommands.ParseAnc(resp);
            Console.Out.WriteLine(state is { } v ? $"已切换: 模式={v.Mode} 档位={v.Level}" : "[未收到确认帧]");
            return 0;
        }
        Console.Out.WriteLine("读请求无响应（协议限制）；状态经连接推送/写入确认帧/耳机按键事件获得。");
        break;

    case "eq":
        if (options.Arguments is ["set", var preset])
        {
            var entry = HuaweiCommands.EqPresets.FirstOrDefault(kv => kv.Value == preset);
            if (entry.Value is null) throw new ArgumentException($"未知预设 {preset}（可用: {FmtPresets()}）");
            await client.SendPackageAsync(HuaweiCommands.EqSetPresetRequest(entry.Key));
            await Task.Delay(300);
        }
        await SendAndPrintAsync(client, HuaweiCommands.EqReadRequest(), p =>
            HuaweiCommands.ParseEqPreset(p) is { } id
                ? HuaweiCommands.EqPresets.GetValueOrDefault((byte)id, $"unknown_{id}")
                : null);
        break;

    case "lowlatency":
        if (options.Arguments is ["on" or "off"])
        {
            await client.SendPackageAsync(HuaweiCommands.LowLatencyWriteRequest(options.Arguments[0] == "on"));
            await Task.Delay(300);
        }
        await SendAndPrintAsync(client, HuaweiCommands.LowLatencyReadRequest(),
            p => HuaweiCommands.ParseLowLatency(p) is { } v ? (v ? "开启" : "关闭") : null);
        break;

    default:
        PrintUsage();
        return 2;
}

await client.StopAsync();
return 0;

static async Task SendAndPrintAsync(HuaweiSppClient client, HuaweiSppPackage request,
    Func<HuaweiSppPackage, string?> describe, params byte[][] acceptIds)
{
    var resp = await client.SendPackageAsync(request, 5000, acceptIds);
    if (resp is null)
    {
        Console.Out.WriteLine("[超时] 未收到响应");
        return;
    }
    var text = describe(resp);
    Console.Out.WriteLine(text is null ? $"[解析失败] {resp}" : $"结果: {text}");
}

static string FmtBattery((int? Global, int? Left, int? Right, int? Case, bool? Charging) b) =>
    $"左={Pct(b.Left)} 右={Pct(b.Right)} 盒={Pct(b.Case)} 全局={Pct(b.Global)} 充电中={(b.Charging == true ? "是" : "否")}";

static string Pct(int? v) => v is null ? "?" : $"{v}%";

static T ParseEnum<T>(string value) where T : struct, Enum =>
    Enum.TryParse<T>(value, ignoreCase: true, out var v)
        ? v
        : throw new ArgumentException($"无效枚举值 {value}（可用: {string.Join('/', Enum.GetNames<T>())}）");

static string FmtPresets() => string.Join('/', HuaweiCommands.EqPresets.Values);

static ulong ParseAddress(string text) =>
    ulong.Parse(text.Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

static void PrintUsage()
{
    Console.Out.WriteLine("""
        用法:
          服务模式（由 EarCtrl.App 拉起）:
            EarCtrl.Agent.Huawei --pipe <pipeName> [--profile <path>] [--log <level>]
          PoC CLI:
            EarCtrl.Agent.Huawei [--address <AA:BB:CC:DD:EE:FF>] <命令> [--verbose]
        命令:
          list                           列出已配对蓝牙设备（含地址与连接状态）
          battery                        读电量
          anc [set <mode> [level]]       读/设降噪；mode=normal|cancellation|awareness, level=normal|comfort|ultra|dynamic
          eq [set <preset>]              读/设 EQ；preset=default|hardbass|treble|voices
          lowlatency [on|off]            读/设低延迟
        """);
}

/// <summary>
/// M2 真机 IPC 闭环冒烟：自起 IPC 服务 + 客户端回环，走完整契约
/// hello → connect(真机) → getState(电量推送) → setAncMode → shutdown。
/// 用法: EarCtrl.Agent.Huawei selftest [--address AA:BB:CC:DD:EE:FF]
/// </summary>
static async Task<int> RunSelfTestAsync(AgentOptions options)
{
    var pipeName = $"earctrl.selftest.{Guid.NewGuid():N}";
    var link = new HuaweiSppClient();
    var server = new NamedPipeServer(pipeName);
    var rpc = new AgentRpc(link, server, new AgentRpcOptions());
    rpc.Start();
    await Task.Delay(300);

    var client = new NamedPipeClient(pipeName);
    var connectionEvents = new List<string>();
    client.EventReceived += e =>
    {
        if (e.Event == "connectionChanged")
        {
            var status = e.Payload?.GetProperty("status").GetString() ?? "?";
            connectionEvents.Add(status);
            Console.Out.WriteLine($"[event] connectionChanged: {status}");
        }
        else if (e.Event == "stateChanged")
        {
            Console.Out.WriteLine($"[event] stateChanged: {e.Payload?.ToString()}");
        }
    };

    try
    {
        await client.ConnectAsync();
        var hello = await client.SendRequestAsync("hello", new { version = "1" });
        Console.Out.WriteLine($"hello: ok={hello.Ok}");

        if (options.Address is not null)
        {
            var address = ulong.Parse(
                options.Address.Replace(":", ""),
                System.Globalization.NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            var connect = await client.SendRequestAsync("connect", new { deviceAddress = options.Address });
            Console.Out.WriteLine($"connect: ok={connect.Ok} 链路={connect.Result?.GetProperty("connection").GetString()}");
            await Task.Delay(500); // 等 0127 电量推送

            var anc = await client.SendRequestAsync("setAncMode", new { mode = "off" });
            Console.Out.WriteLine($"setAncMode(off): ok={anc.Ok} anc={anc.Result?.GetProperty("anc")}");
        }
        else
        {
            Console.Out.WriteLine("未提供 --address，跳过真机连接（IPC 闭环已验证）");
        }

        var state = await client.SendRequestAsync("getState");
        Console.Out.WriteLine($"getState: {state.Result}");

        var shutdown = await client.SendRequestAsync("shutdown");
        Console.Out.WriteLine($"shutdown: ok={shutdown.Ok}");

        await rpc.ShutdownSignal.WaitAsync(TimeSpan.FromSeconds(3));
        Console.Out.WriteLine($"connectionEvents: [{string.Join(" → ", connectionEvents)}]");
        Console.Out.WriteLine("SELFTEST PASS");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"SELFTEST FAIL: {ex.Message}");
        return 1;
    }
    finally
    {
        await client.DisposeAsync();
        await rpc.DisposeAsync();
    }
}

/// <summary>M2 服务模式：单实例幂等 → 起 NamedPipe 服务 → AgentRpc 接管；孤儿/关闭信号驱动退出。</summary>
static async Task<int> RunServiceModeAsync(AgentOptions options)
{
    // 单实例幂等（§3.1-B）：管道已存在说明 Agent 在跑，直接退出（复用语义）
    try
    {
        var probe = new NamedPipeClient(options.PipeName!);
        await using (probe.ConfigureAwait(false))
        {
            await probe.ConnectAsync();
        }
        Console.Out.WriteLine($"[service] {options.PipeName} 已有实例在运行，本进程退出（复用现有实例）");
        return 0;
    }
    catch
    {
        // 连不上 = 无既有实例，继续
    }

    var link = new HuaweiSppClient();
    var server = new NamedPipeServer(options.PipeName!);
    var rpc = new AgentRpc(link, server, new AgentRpcOptions());
    rpc.Start();
    Console.Out.WriteLine($"[service] IPC 服务端就绪: {options.PipeName}");

    await rpc.ShutdownSignal;
    Console.Out.WriteLine("[service] graceful exit (0)");
    await rpc.DisposeAsync();
    return 0;
}
