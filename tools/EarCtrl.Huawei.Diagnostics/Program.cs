// SPDX-License-Identifier: GPL-3.0-only
// 华为耳机真机诊断工具（开发期专用，不随 EarCtrl.App 发行）。
// 从主 Agent 的 PoC CLI 迁出（M3.5 收尾），命令一览：
//   list / selftest                       —— 设备列举 / M2 IPC 闭环冒烟
//   battery / anc / eq / lowlatency       —— M1 功能读写验证
//   monitor                               —— 纯监听全量帧采集（零轮询）
//   monitor2                              —— 官方 7 帧核心初始化序列重放（已证不激活推送，留档）
//   monitor3                              —— 官方 78 帧完整初始化重放（已证不激活推送，留档）
//   polltest                              —— 「2B70 使能 → 0108 查询」成对轮询回帧率诊断
// 协议实验定论见 .trae/documents/protocol-huawei-se4anc.md §7。

using System.Globalization;
using EarCtrl.Agents.Huawei;
using EarCtrl.Agents.Huawei.Commands;
using EarCtrl.Agents.Huawei.Lpv2;
using EarCtrl.Ipc.NamedPipe;

var options = AgentOptions.Parse(args);

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

switch (options.Command)
{
    case "polltest":
    {
        // 轮询回帧率诊断：周期发送「2B70 使能 → 0108 查询」，全量打印 TX/RX 与统计
        var t0 = DateTime.Now;
        var tx = 0;
        var rx = 0;
        client.RawFrame += (dir, bytes) =>
        {
            if (dir == "TX") Interlocked.Increment(ref tx);
            else Interlocked.Increment(ref rx);
            Console.Out.WriteLine($"{DateTime.Now - t0:mm\\:ss\\.fff} [{dir}] {Convert.ToHexString(bytes)}");
        };
        void PrintBattery(HuaweiSppPackage pkg)
        {
            var p5 = pkg.FindParam(5);
            Console.Out.WriteLine($"      ↳ 电量帧 {FmtBattery(HuaweiCommands.ParseBattery(pkg))} param5={Convert.ToHexString(p5)}");
        }
        client.OnNotify(Cmd.BatteryNotify, pkg => { PrintBattery(pkg); return Task.CompletedTask; });
        client.OnNotify(Cmd.BatteryRead, pkg => { PrintBattery(pkg); return Task.CompletedTask; });
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    // 定论：2B70 是查询使能帧——不发它，耳机对 0108 一律不回（纯 0108 轮询 37 发 0 回）
                    await client.SendPackageAsync(HuaweiSppPackage.ChangeRequestNoWait(Cmd.KeepAlive, (1, [0xCB])));
                }
                catch (Exception ex) { Console.Out.WriteLine($"TX(2B70) 失败: {ex.Message}"); }
                await Task.Delay(200);
                try
                {
                    await client.SendPackageAsync(HuaweiCommands.BatteryReadNoWaitRequest());
                }
                catch (Exception ex) { Console.Out.WriteLine($"TX 失败: {ex.Message}"); }
                await Task.Delay(1000);
            }
        });
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(10000);
                Console.Out.WriteLine($"-- 统计: TX={tx} RX={rx} --");
            }
        });
        Console.Out.WriteLine("1s 轮询诊断中（期间可做开关盒动作），Ctrl+C 退出…");
        await Task.Delay(Timeout.Infinite);
        break;
    }

    case "monitor3":
    {
        // 完整复刻实验：按官方 App 连接后 5.77~8.0s 的全部 78 帧按序重放，然后纯监听。
        // 结论（留档）：推送仍未激活——T0026 对第三方 PC 客户端不做事件推送
        var t0 = DateTime.Now;
        client.RawFrame += (dir, bytes) =>
            Console.Out.WriteLine($"{DateTime.Now - t0:mm\\:ss\\.fff} [{dir}] {Convert.ToHexString(bytes)}");
        client.OnNotify(Cmd.BatteryNotify, pkg =>
        {
            var p5 = pkg.FindParam(5);
            Console.Out.WriteLine($"      ↳ 0127 电量帧 {FmtBattery(HuaweiCommands.ParseBattery(pkg))} param5={Convert.ToHexString(p5)}");
            return Task.CompletedTask;
        });

        string[] officialFrames =
        [
            "5A0009000108010002000300FBB9",
            "5A0005002B700101CB50",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0009000108010002000300FBB9",
            "5A0005002B0901009DE8",
            "5A000B002BB401013002010103007B6A",
            "5A0009000108010002000300FBB9",
            "5A0005002B110100772A",
            "5A0009000108010002000300FBB9",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0005002B2D0100C7EE",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A000A000C0101057A682D434E745D",
            "5A000D00010501046AC5129502020800677E",
            "5A0008002BB401011D0200594B",
            "5A0005002B2A0100427E",
            "5A0005002B4A02008C46",
            "5A0009000108010002000300FBB9",
            "5A00040001071CEDC5",
            "5A0011002B0A0100020003000400050006000700E949",
            "5A0005002B700101CB50",
            "5A0005002B700101CB50",
            "5A0005002B700101CB50",
            "5A0006002B490101012F1A",
            "5A0005002B700101CB50",
            "5A00350001310130333233636432343933393963613531303463666230333139306166383035393734323237613930386332383934343836D8FF",
            "5A005500010723505265646D6900000000000000000000003233303133524B373543000000000000415133412E3235303232362E3030320000000000000000000000000000000000416E64726F69642031350000000000009E3F",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0008002BB4010123020087EF",
            "5A00040001071CEDC5",
            "5A0005002B700101CB50",
            "5A00350001310130333233636432343933393963613531303463666230333139306166383035393734323237613930386332383934343836D8FF",
            "5A001F0001070100020003000400050006000700080009000A000B000C000F00190002D9",
            "5A0005002B250300082D",
            "5A0005002B4A02008C46",
            "5A0009002BAC0101010201013CCA",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0047002BB30101010A010034000201000301000401000901000B01000C01000E0100120100210011000701001401011601001501001901003201012501002901002A00360100310100D15B",
            "5A0009000108010002000300FBB9",
            "5A0006002B490101012F1A",
            "5A0009000108010002000300FBB9",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0005002B0901009DE8",
            "5A0005002B250300082D",
            "5A0005002B4A02008C46",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0047002BB30101010A010034000201000301000401000901000B01000C01000E0100120100210011000701001401011601001501001901003201012501002901002A00360100310100D15B",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0005002B0901009DE8",
            "5A0005002B250300082D",
            "5A0009000108010002000300FBB9",
            "5A0005002B4A02008C46",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0008002BB401011D0200594B",
            "5A0005002B2A0100427E",
            "5A0005002B4A02008C46",
            "5A0047002BB30101010A010034000201000301000401000901000B01000C01000E0100120100210011000701001401011601001501001901003201012501002901002A00360100310100D15B",
            "5A0005002B0901009DE8",
            "5A0005002B250300082D",
            "5A0005002B4A02008C46",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
            "5A0047002BB30101010A010034000201000301000401000901000B01000C01000E0100120100210011000701001401011601001501001901003201012501002901002A00360100310100D15B",
            "5A0006002B490101012F1A",
            "5A0006002B490101012F1A",
            "5A0006002B490101012F1A",
            "5A0006002B490101012F1A",
            "5A00040001071CEDC5",
            "5A00040001071CEDC5",
            "5A00040001071CEDC5",
            "5A0005002B700101CB50",
            "5A0005002B700101CB50",
            "5A0005002B700101CB50",
            "5A00350001310130333233636432343933393963613531303463666230333139306166383035393734323237613930386332383934343836D8FF",
            "5A00350001310130333233636432343933393963613531303463666230333139306166383035393734323237613930386332383934343836D8FF",
            "5A00350001310130333233636432343933393963613531303463666230333139306166383035393734323237613930386332383934343836D8FF",
        ];

        await Task.Delay(1000);
        Console.Out.WriteLine($"== 重放官方初始化序列（{officialFrames.Length} 帧） ==");
        foreach (var hex in officialFrames)
        {
            try
            {
                await client.SendPackageAsync(HuaweiSppPackage.FromBytes(Convert.FromHexString(hex)));
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine($"TX 失败: {ex.Message}");
            }
            await Task.Delay(30); // 官方节奏 ≈28ms/帧
        }
        Console.Out.WriteLine("== 重放完毕，纯监听中，做动作链，Ctrl+C 退出 ==");
        await Task.Delay(Timeout.Infinite);
        break;
    }

    case "monitor2":
    {
        // 官方核心初始化序列重放（结论留档：推送不激活，官方手机上的及时性靠轮询风暴）
        var t0 = DateTime.Now;
        client.RawFrame += (dir, bytes) =>
            Console.Out.WriteLine($"{DateTime.Now - t0:mm\\:ss\\.fff} [{dir}] {Convert.ToHexString(bytes)}");
        client.OnNotify(Cmd.BatteryNotify, pkg =>
        {
            var p5 = pkg.FindParam(5);
            Console.Out.WriteLine($"      ↳ 0127 电量帧 {FmtBattery(HuaweiCommands.ParseBattery(pkg))} param5={Convert.ToHexString(p5)}");
            return Task.CompletedTask;
        });

        await Task.Delay(1000);
        Console.Out.WriteLine("== 发送官方初始化序列 ==");
        foreach (var (desc, pkg) in ExperimentalCommands.OfficialSessionInit())
        {
            try
            {
                pkg.ResponseId = []; // 全部 no-wait：回帧全部走 notify 打印，不被 pending 吞掉
                await client.SendPackageAsync(pkg);
                Console.Out.WriteLine($"TX {desc}");
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine($"TX {desc} 失败: {ex.Message}");
            }
            await Task.Delay(300);
        }
        Console.Out.WriteLine("== 序列完毕，纯监听中，做动作链，Ctrl+C 退出 ==");
        await Task.Delay(Timeout.Infinite);
        break;
    }

    case "monitor":
    {
        // 真机帧采集（纯监听，零轮询零干扰）：全量打印 TX/RX 原始帧 hex + 关键推送解析
        var t0 = DateTime.Now;
        client.RawFrame += (dir, bytes) =>
            Console.Out.WriteLine($"{DateTime.Now - t0:mm\\:ss\\.fff} [{dir}] {Convert.ToHexString(bytes)}");
        client.OnNotify(Cmd.BatteryNotify, pkg =>
        {
            var p5 = pkg.FindParam(5);
            Console.Out.WriteLine($"      ↳ 0127 电量帧 {FmtBattery(HuaweiCommands.ParseBattery(pkg))} param5={Convert.ToHexString(p5)}");
            return Task.CompletedTask;
        });
        Console.Out.WriteLine("全量监听中（无轮询），Ctrl+C 退出…");
        await Task.Delay(Timeout.Infinite);
        break;
    }

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
        用法 (EarCtrl.Huawei.Diagnostics):
          EarCtrl.Huawei.Diagnostics [--address <AA:BB:CC:DD:EE:FF>] <命令> [--verbose]
        命令:
          list                           列出已配对蓝牙设备（含地址与连接状态）
          battery                        读电量
          anc [set <mode> [level]]       读/设降噪；mode=normal|cancellation|awareness, level=normal|comfort|ultra|dynamic
          eq [set <preset>]              读/设 EQ；preset=default|hardbass|treble|voices
          lowlatency [on|off]            读/设低延迟
          selftest [--address <addr>]    M2 IPC 闭环冒烟
          monitor                        纯监听全量帧采集（零轮询）
          monitor2 / monitor3            官方初始化序列重放实验（结论已留档协议笔记 §7）
          polltest                       「2B70 使能 → 0108 查询」成对轮询回帧率诊断
        """);
}

/// <summary>
/// M2 真机 IPC 闭环冒烟：自起 IPC 服务 + 客户端回环，走完整契约
/// hello → connect(真机) → getState(电量推送) → setAncMode → shutdown。
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

/// <summary>
/// 官方智慧音频 App 会话初始化实验帧（字节级复刻，来源见协议笔记 §7.3）。
/// 留档：monitor3 全量重放已证该序列无法在 PC 侧激活事件推送。
/// </summary>
internal static class ExperimentalCommands
{
    public static readonly byte[] WearStatusRead = [0x2B, 0x25]; // GET_WEARING_STATUS

    public static IEnumerable<(string Desc, HuaweiSppPackage Pkg)> OfficialSessionInit()
    {
        // ① 2B70 01 01 CB：静默升级查询/查询使能
        yield return ("2B70 silent-upgrade query",
            HuaweiSppPackage.ChangeRequestNoWait([0x2B, 0x70], (1, [0xCB])));
        // ② 0108 电量读
        yield return ("0108 battery read", HuaweiCommands.BatteryReadRequest());
        // ③ 2B25 佩戴检测开关读
        yield return ("2B25 wear-switch read",
            HuaweiSppPackage.ReadRequest(WearStatusRead, 1, 2));
        // ④ 2B25 在盒状态读
        yield return ("2B25 inbox read",
            HuaweiSppPackage.ReadRequest(WearStatusRead, 3));
        // ⑤ 0107 能力批量读（官方 A0()：params 1..12,15,24,25）+ param35 手机信息（官方 Cn()）
        var info = HuaweiSppPackage.ReadRequest([0x01, 0x07], 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 24, 25);
        info.Parameters[35] = BuildPhoneInfo();
        yield return ("0107 info + phone-info", info);
        // ⑥ 0105 时间同步（param1=UTC 秒 BE，param2=[时区小时(负数|0x80), 分钟]）
        var utc = BitConverter.GetBytes((uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0xFFFFFFFF));
        if (BitConverter.IsLittleEndian) Array.Reverse(utc);
        var off = DateTimeOffset.Now.Offset;
        var hour = (byte)(Math.Abs(off.Hours) | (off.Hours < 0 ? 0x80 : 0x00));
        yield return ("0105 time sync",
            HuaweiSppPackage.ChangeRequestNoWait([0x01, 0x05], (1, utc), (2, [hour, (byte)Math.Abs(off.Minutes)])));
        // ⑦ 0C01 语言/单位（官方抓包为 "zh-CN" 5 字节）
        yield return ("0C01 language",
            HuaweiSppPackage.ChangeRequestNoWait([0x0C, 0x01],
                (1, System.Text.Encoding.UTF8.GetBytes("zh-CN"))));
    }

    /// <summary>0107 param35 手机信息 80 字节（官方 Cn() 布局：BRAND16 + MODEL16 + DISPLAY32 + OS16）。</summary>
    private static byte[] BuildPhoneInfo()
    {
        var buf = new byte[80];
        Put(buf, 0, 16, "Microsoft");
        Put(buf, 16, 16, Environment.MachineName);
        Put(buf, 32, 32, Environment.OSVersion.VersionString);
        Put(buf, 64, 16, "Windows 11");
        return buf;

        static void Put(byte[] buf, int off, int len, string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            Array.Copy(b, 0, buf, off, Math.Min(b.Length, len));
        }
    }
}
