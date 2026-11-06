// SPDX-License-Identifier: GPL-3.0-only
// 华为命令表与 SE 4 ANC 适配的命令构造（借鉴 OpenFreebuds constants/spp_commands.py 与各 handler 的设计，
// 并经真机实测与官方智慧音频 App 逆向大幅修正，差异详见 .trae/documents/protocol-huawei-se4anc.md）。

using EarCtrl.Agents.Huawei.Lpv2;

namespace EarCtrl.Agents.Huawei.Commands;

public static class Cmd
{
    public static readonly byte[] BatteryRead = [0x01, 0x08];
    public static readonly byte[] BatteryNotify = [0x01, 0x27];
    public static readonly byte[] AncRead = [0x2B, 0x2A];
    public static readonly byte[] AncWrite = [0x2B, 0x04];
    public static readonly byte[] AncChangeEvent = [0x2B, 0x03];
    public static readonly byte[] LowLatency = [0x2B, 0x6C];
    public static readonly byte[] EqRead = [0x2B, 0x4A];
    public static readonly byte[] EqWrite = [0x2B, 0x49];
    public static readonly byte[] KeepAlive = [0x2B, 0x70];      // 查询使能/保活帧（真机实测：不发它，0108 一律不回帧）
    /// <summary>子功能开关总线（官方 MbbAbilityManager：param1=[子功能号]，param2=[开关状态]）。
    /// 0x1D=防风噪（官方 FeatureMbbState.WIND_NOISE_PREVENTION，featureKey=29）。</summary>
    public static readonly byte[] MbbFeatureSwitch = [0x2B, 0xB4];
    /// <summary>连击（五下）子功能号（官方 MultipleClickEvent.ID_MULTIPLE_CLICK_FUNCTION=20）。</summary>
    public const byte MultiTapFeatureId = 0x14;
    /// <summary>双击动作读/写（官方 DoubleClickEvent：读 01 20、写 01 1F）。</summary>
    public static readonly byte[] DualTapRead = [0x01, 0x20];
    public static readonly byte[] DualTapWrite = [0x01, 0x1F];
    /// <summary>三击动作读/写（官方 TripleClickEvent：读 01 26、写 01 25）。</summary>
    public static readonly byte[] TripleTapRead = [0x01, 0x26];
    public static readonly byte[] TripleTapWrite = [0x01, 0x25];
    /// <summary>长按（触控）动作读：params[1,2]=左/右耳（OpenFreebuds action_long_tap_split，SE 4 同款）。</summary>
    public static readonly byte[] LongTapRead = [0x2B, 0x17];
    /// <summary>长按动作写：param1=左 / param2=右，单字节有符号值。</summary>
    public static readonly byte[] LongTapWrite = [0x2B, 0x16];
    /// <summary>长按 ANC 循环选项读：params[1,2]=左/右耳。</summary>
    public static readonly byte[] LongTapAncRead = [0x2B, 0x19];
    /// <summary>长按 ANC 循环选项写：param1=左 / param2=右，单字节有符号值。</summary>
    public static readonly byte[] LongTapAncWrite = [0x2B, 0x18];
}

public enum AncMode
{
    Normal = 0,       // 关闭
    Cancellation = 1, // 降噪
    Awareness = 2,    // 透传
}

public enum AncLevel
{
    Normal = 0,  // 均衡
    Comfort = 1, // 轻度
    Ultra = 2,   // 深度
    Dynamic = 3, // 动态（SE 4 ANC 支持，w_cancel_dynamic=True）
}

public static class HuaweiCommands
{
    // ---- 电量（handler/battery.py） ----

    public static HuaweiSppPackage BatteryReadRequest() =>
        HuaweiSppPackage.ReadRequest(Cmd.BatteryRead, 1, 2, 3);

    /// <summary>电量读请求（不等待响应版）：0108 查询（需先发 2B70 使能帧，回帧 cmd=0108）。</summary>
    public static HuaweiSppPackage BatteryReadNoWaitRequest()
    {
        var pkg = BatteryReadRequest();
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>解析电量响应/推送。w_tws=True：param2 为 左,右,盒 三个字节。</summary>
    /// <remarks>
    /// param3 = [左耳, 右耳, 充电盒] 充电标志（官方 BatteryResult 解析 → BatteryPercent.setChargingState，
    /// UI 判据 chargingState[i] &gt; 0 = 充电中）。<see cref="CaseCharging"/> 取第三个字节。
    /// </remarks>
    public static (int? Global, int? Left, int? Right, int? Case, bool? Charging, bool? CaseCharging)
        ParseBattery(HuaweiSppPackage pkg)
    {
        int? global = null, left = null, right = null, caseLevel = null;
        bool? charging = null, caseCharging = null;

        var p1 = pkg.FindParam(1);
        if (p1.Length == 1)
        {
            global = p1[0];
        }

        var p2 = pkg.FindParam(2);
        if (p2.Length == 3)
        {
            left = p2[0];
            right = p2[1];
            caseLevel = p2[2];
        }

        var p3 = pkg.FindParam(3);
        if (p3.Length > 0)
        {
            charging = p3.Any(b => b > 0);
            if (p3.Length >= 3)
            {
                caseCharging = p3[2] > 0;
            }
        }

        return (global, left, right, caseLevel, charging, caseCharging);
    }

    // ---- ANC（handler/anc.py，SE 4 ANC: w_cancel_lvl=True, w_cancel_dynamic=True） ----

    public static HuaweiSppPackage AncReadRequest() =>
        HuaweiSppPackage.ReadRequest(Cmd.AncRead, 1, 2);

    /// <summary>ANC 状态读请求（不等待响应版）：2B70 使能帧开路后发送，回报帧经 OnNotify(2B2A) 驱动状态。</summary>
    public static HuaweiSppPackage AncReadNoWaitRequest()
    {
        var pkg = AncReadRequest();
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>解析 ANC 读取响应：param1 = [level, mode] 两字节。</summary>
    public static (AncMode Mode, AncLevel Level)? ParseAnc(HuaweiSppPackage pkg)
    {
        var data = pkg.FindParam(1);
        return data.Length == 2 ? ((AncMode)data[1], (AncLevel)data[0]) : null;
    }

    /// <summary>切换降噪模式（对应 anc.py set_property("mode")）。</summary>
    public static HuaweiSppPackage AncSetModeRequest(AncMode mode)
    {
        var modeByte = (byte)mode;
        var data = mode == AncMode.Normal
            ? new[] { modeByte, (byte)0x00 }
            : new[] { modeByte, (byte)0xFF };
        return HuaweiSppPackage.ChangeRequest(Cmd.AncWrite, (1, data));
    }

    /// <summary>仅调整降噪档位（保持当前模式，对应 anc.py set_property(level)）。</summary>
    public static HuaweiSppPackage AncSetLevelRequest(AncMode activeMode, AncLevel level)
    {
        var data = new[] { (byte)activeMode, (byte)level };
        return HuaweiSppPackage.ChangeRequest(Cmd.AncWrite, (1, data));
    }

    // ---- 低延迟（handler/low_latency.py） ----

    public static HuaweiSppPackage LowLatencyReadRequest() =>
        HuaweiSppPackage.ReadRequest(Cmd.LowLatency, 2);

    public static bool? ParseLowLatency(HuaweiSppPackage pkg)
    {
        var v = pkg.FindParam(2);
        return v.Length == 0 ? null : v[0] == 1;
    }

    public static HuaweiSppPackage LowLatencyWriteRequest(bool enabled) =>
        HuaweiSppPackage.ChangeRequest(Cmd.LowLatency, (1, enabled ? new byte[] { 0x01 } : new byte[] { 0x00 }));

    // ---- EQ（handler/config_equalizer.py，SE 4 ANC 内置预设） ----

    public static readonly IReadOnlyDictionary<byte, string> EqPresets = new Dictionary<byte, string>
    {
        [1] = "default",
        [2] = "hardbass",
        [3] = "treble",
        [9] = "voices",
    };

    public static HuaweiSppPackage EqReadRequest() =>
        HuaweiSppPackage.ReadRequest(Cmd.EqRead, 1, 2, 3, 4, 5, 6, 7, 8);

    /// <summary>EQ 读请求不等待响应版（会话初始化用，回帧走 OnNotify(2B4A)）。</summary>
    public static HuaweiSppPackage EqReadNoWaitRequest()
    {
        var pkg = EqReadRequest();
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>切换到内置预设（SE 4 ANC 无自定义 EQ 写场景时的最简路径）。</summary>
    public static HuaweiSppPackage EqSetPresetRequest(byte presetId) =>
        HuaweiSppPackage.ChangeRequest(Cmd.EqWrite, (1, new[] { presetId }));

    /// <summary>解析当前 EQ 预设 id（param2 单字节，有符号）。</summary>
    public static int? ParseEqPreset(HuaweiSppPackage pkg)
    {
        var v = pkg.FindParam(2);
        return v.Length == 1 ? (sbyte)v[0] : null;
    }

    /// <summary>自定义 EQ 模式 id 起点（官方/OpenFreebuds 约定：内置为 1..9，自定义从 100 起）。</summary>
    public const int EqCustomIdBase = 100;

    /// <summary>自定义 EQ 频段数量（官方自定义界面：60/125/250/500/1K/2K/4K/8K/12K/16K）。</summary>
    public const int EqCustomBandCount = 10;

    /// <summary>
    /// 解析 EQ 读回的自定义模式列表（param8）：每条 36 字节 = id(1) + count(1) + data[count] + name(UTF-8, 以 0 结束)。
    /// 频段值为有符号字节（官方界面范围 -6..+6）。
    /// </summary>
    public static IReadOnlyList<(int Id, string Name, int[] Bands)>? ParseEqCustomModes(HuaweiSppPackage pkg)
    {
        var raw = pkg.FindParam(8);
        if (raw.Length == 0)
        {
            return null;
        }

        var list = new List<(int Id, string Name, int[] Bands)>();
        for (var offset = 0; offset + 2 <= raw.Length; offset += 36)
        {
            var length = Math.Min(36, raw.Length - offset);
            var slice = raw.AsSpan(offset, length);
            var id = (sbyte)slice[0];
            var count = slice[1];
            if (count < 1 || count > 30 || 2 + count > slice.Length)
            {
                continue;
            }

            var bands = new int[count];
            for (var i = 0; i < count; i++)
            {
                bands[i] = (sbyte)slice[2 + i];
            }

            var nameBytes = slice[(2 + count)..].ToArray();
            var nul = Array.IndexOf(nameBytes, (byte)0);
            if (nul >= 0)
            {
                nameBytes = nameBytes[..nul];
            }
            var name = System.Text.Encoding.UTF8.GetString(nameBytes).Trim();
            list.Add((id, name, bands));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 自定义 EQ 写入（2B49）：param1=模式 id（自定义从 100 起）、param2=频段数、param3=频段值、
    /// param4=名称（UTF-8）、param5=动作（1=保存并应用 / 2=删除）。官方 V3 构造与 OpenFreebuds 一致。
    /// </summary>
    public static HuaweiSppPackage EqCustomSetRequest(int id, string name, IReadOnlyList<int> bands, byte action)
    {
        var data = bands.Select(v => (byte)(sbyte)v).ToArray();
        return HuaweiSppPackage.ChangeRequest(Cmd.EqWrite,
            (1, [(byte)id]),
            (2, [(byte)data.Length]),
            (3, data),
            (4, System.Text.Encoding.UTF8.GetBytes(name)),
            (5, [action]));
    }

    /// <summary>删除自定义 EQ：带原频段数据与名称，动作值 2。</summary>
    public static HuaweiSppPackage EqCustomDeleteRequest(int id, string name, IReadOnlyList<int> bands) =>
        EqCustomSetRequest(id, name, bands, action: 2);

    // ---- 防风噪（官方智慧音频 App 逆向：2B B4 子功能总线，协议笔记 §7.8） ----

    /// <summary>防风噪子功能号（官方 FeatureMbbState.WIND_NOISE_PREVENTION，featureKey=29）。</summary>
    public const byte WindNoiseFeatureId = 0x1D;

    /// <summary>防风噪状态读请求：2B B4 | 01 01 1D | 02 00（param2 空值=读）。</summary>
    public static HuaweiSppPackage WindNoiseReadRequest() =>
        HuaweiSppPackage.ChangeRequest(Cmd.MbbFeatureSwitch, (1, [WindNoiseFeatureId]), (2, []));

    /// <summary>读请求不等待响应版（会话初始化用，回帧走 OnNotify）。</summary>
    public static HuaweiSppPackage WindNoiseReadNoWaitRequest()
    {
        var pkg = WindNoiseReadRequest();
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>防风噪开关写入：2B B4 | 01 01 1D | 02 01 [01=开 / 00=关]。</summary>
    public static HuaweiSppPackage WindNoiseSetRequest(bool enabled) =>
        HuaweiSppPackage.ChangeRequest(Cmd.MbbFeatureSwitch,
            (1, [WindNoiseFeatureId]), (2, enabled ? [0x01] : [0x00]));

    /// <summary>防风噪开关写入（不等待确认帧版）：IPC setWindNoise 用——写完即回，
    /// 状态由耳机回报帧异步驱动；避免快速切换时确认等待堆积触发 App 侧 IPC 8s 超时。</summary>
    public static HuaweiSppPackage WindNoiseSetNoWaitRequest(bool enabled)
    {
        var pkg = WindNoiseSetRequest(enabled);
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>
    /// 解析防风噪回报/确认帧（param1=[0x1D]，param2=[状态]）。
    /// 耳机回报帧与写帧同构（官方 t1→l2 解析路径），状态不是本功能时返回 null。
    /// </summary>
    public static bool? ParseWindNoise(HuaweiSppPackage pkg)
    {
        var feature = pkg.FindParam(1);
        if (feature.Length != 1 || feature[0] != WindNoiseFeatureId) return null;
        var state = pkg.FindParam(2);
        return state.Length == 1 ? state[0] == 0x01 : null;
    }

    // ---- 长按（触控）设置（OpenFreebuds action_long_tap_split，SE 4 同款；协议笔记 §7.11）----

    /// <summary>长按动作读请求：2B17 params[1,2]=左/右耳（空值读）。</summary>
    public static HuaweiSppPackage LongTapReadNoWaitRequest()
    {
        var pkg = HuaweiSppPackage.ChangeRequest(Cmd.LongTapRead, (1, []), (2, []));
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>长按 ANC 循环选项读请求：2B19 params[1,2]=左/右耳（空值读）。</summary>
    public static HuaweiSppPackage LongTapAncReadNoWaitRequest()
    {
        var pkg = HuaweiSppPackage.ChangeRequest(Cmd.LongTapAncRead, (1, []), (2, []));
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>
    /// 长按动作写：2B16 param(side)=值。
    /// side 1=左耳 / 2=右耳 / 4=通话中；值（sbyte）：-1=关闭、10=切换降噪、0=语音助手、18=音量+、19=音量-（通话中为 -1/0=接听）。
    /// </summary>
    public static HuaweiSppPackage LongTapSetRequest(int side, sbyte value) =>
        HuaweiSppPackage.ChangeRequestNoWait(Cmd.LongTapWrite, (side, [(byte)value]));

    /// <summary>长按 ANC 循环选项写：2B18 param(side)=值（1..4 见 Profile ancCycleOptions）。</summary>
    public static HuaweiSppPackage LongTapAncSetRequest(int side, sbyte value) =>
        HuaweiSppPackage.ChangeRequestNoWait(Cmd.LongTapAncWrite, (side, [(byte)value]));

    /// <summary>解析长按动作回报（2B17）：param1/2=左右耳动作、param4=通话中动作（均为单字节有符号值）。</summary>
    public static (int? Left, int? Right, int? InCall)? ParseLongTap(HuaweiSppPackage pkg)
    {
        var left = SignedParam(pkg, 1);
        var right = SignedParam(pkg, 2);
        var inCall = SignedParam(pkg, 4);
        if (left is null && right is null && inCall is null) return null;
        return (left, right, inCall);
    }

    /// <summary>解析长按 ANC 循环口径（2B19）：param1/2=左/右耳选项值。</summary>
    public static (int? Left, int? Right)? ParseLongTapAncCycle(HuaweiSppPackage pkg)
    {
        var left = SignedParam(pkg, 1);
        var right = SignedParam(pkg, 2);
        if (left is null && right is null) return null;
        return (left, right);
    }

    private static int? SignedParam(HuaweiSppPackage pkg, int type)
    {
        var value = pkg.FindParam(type);
        return value.Length == 1 ? (sbyte)value[0] : null;
    }

    // ---- 双击 / 三击 / 连击（官方逆向与 OpenFreebuds 对照，协议笔记 §7.10）----

    /// <summary>双击动作读请求：01 20 params[1,2,4]（左/右/通话中，空值读）。</summary>
    public static HuaweiSppPackage DualTapReadNoWaitRequest()
    {
        var pkg = HuaweiSppPackage.ChangeRequest(Cmd.DualTapRead, (1, []), (2, []), (4, []));
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>三击动作读请求：01 26 params[1,2]（左/右，空值读）。</summary>
    public static HuaweiSppPackage TripleTapReadNoWaitRequest()
    {
        var pkg = HuaweiSppPackage.ChangeRequest(Cmd.TripleTapRead, (1, []), (2, []));
        pkg.ResponseId = [];
        return pkg;
    }

    /// <summary>连击（五下）读请求：2B B4 | 01 01 14 | 04 00（读可用值列表，回帧带当前左右值）。</summary>
    public static HuaweiSppPackage MultiTapReadNoWaitRequest() =>
        HuaweiSppPackage.ChangeRequestNoWait(Cmd.MbbFeatureSwitch, (1, [Cmd.MultiTapFeatureId]), (4, []));

    /// <summary>双击/三击动作写：side 1=左 / 2=右 / 4=通话中；值见 profile 选项（sbyte）。</summary>
    public static HuaweiSppPackage TapSetRequest(byte[] writeCommand, int side, sbyte value) =>
        HuaweiSppPackage.ChangeRequestNoWait(writeCommand, (side, [(byte)value]));

    /// <summary>连击（五下）写：官方 param2=左 / param3=右（注意与双击 param1/2 编号不同）。</summary>
    public static HuaweiSppPackage MultiTapSetRequest(bool right, sbyte value) =>
        HuaweiSppPackage.ChangeRequestNoWait(Cmd.MbbFeatureSwitch,
            (1, [Cmd.MultiTapFeatureId]), (right ? 3 : 2, [(byte)value]));

    /// <summary>解析双击/三击回报：param1/2=左右耳、param4=通话中（单字节有符号值）。</summary>
    public static (int? Left, int? Right, int? InCall)? ParseTap(HuaweiSppPackage pkg)
    {
        var left = SignedParam(pkg, 1);
        var right = SignedParam(pkg, 2);
        var inCall = SignedParam(pkg, 4);
        if (left is null && right is null && inCall is null) return null;
        return (left, right, inCall);
    }

    /// <summary>解析连击回报：param2=左、param3=右（官方 MultipleClick 解析器）。</summary>
    public static (int? Left, int? Right)? ParseMultiTap(HuaweiSppPackage pkg)
    {
        var left = SignedParam(pkg, 2);
        var right = SignedParam(pkg, 3);
        if (left is null && right is null) return null;
        return (left, right);
    }
}
