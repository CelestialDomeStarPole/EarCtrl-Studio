// SPDX-License-Identifier: GPL-3.0-only
// 华为命令表与 SE 4 ANC（per_model/buds_se_4.py）适配的命令构造（转写自 constants/spp_commands.py 与各 handler）。

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

    // ---- 设备信息（handler/info.py，连接后首条，兼作会话激活） ----

    public static readonly byte[] InfoRead = [0x01, 0x07];

    public static HuaweiSppPackage InfoReadRequest() =>
        HuaweiSppPackage.ReadRequest(InfoRead, Enumerable.Range(0, 32).ToArray());

    public static Dictionary<string, string> ParseInfo(HuaweiSppPackage pkg)
    {
        Dictionary<int, string> descriptor = new()
        {
            [3] = "hardware_ver",
            [7] = "software_ver",
            [9] = "serial_number",
            [10] = "device_submodel",
            [15] = "device_model",
        };
        var outDict = new Dictionary<string, string>();
        foreach (var (key, value) in pkg.Parameters)
        {
            outDict[descriptor.GetValueOrDefault(key, $"field_{key}")] =
                System.Text.Encoding.UTF8.GetString(value).Trim('\0');
        }
        return outDict;
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
}
