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
    public static (int? Global, int? Left, int? Right, int? Case, bool? Charging) ParseBattery(HuaweiSppPackage pkg)
    {
        int? global = null, left = null, right = null, caseLevel = null;
        bool? charging = null;

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
            charging = p3.Contains((byte)0x01);
        }

        return (global, left, right, caseLevel, charging);
    }

    // ---- ANC（handler/anc.py，SE 4 ANC: w_cancel_lvl=True, w_cancel_dynamic=True） ----

    public static HuaweiSppPackage AncReadRequest() =>
        HuaweiSppPackage.ReadRequest(Cmd.AncRead, 1, 2);

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

    /// <summary>切换到内置预设（SE 4 ANC 无自定义 EQ 写场景时的最简路径）。</summary>
    public static HuaweiSppPackage EqSetPresetRequest(byte presetId) =>
        HuaweiSppPackage.ChangeRequest(Cmd.EqWrite, (1, new[] { presetId }));

    /// <summary>解析当前 EQ 预设 id（param2 单字节，有符号）。</summary>
    public static int? ParseEqPreset(HuaweiSppPackage pkg)
    {
        var v = pkg.FindParam(2);
        return v.Length == 1 ? (sbyte)v[0] : null;
    }

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
}
