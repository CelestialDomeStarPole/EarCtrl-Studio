// SPDX-License-Identifier: GPL-3.0-only
// 黄金样例单测：字节样例取自 OpenFreebuds openfreebuds/driver/huawei/test/test_battery.py（GPL-3.0）。
// 这些帧是真实设备抓包/参考实现产物，作为编码器与解析器的行为基准，不连耳机即可回归。

using EarCtrl.Agents.Huawei.Commands;
using EarCtrl.Agents.Huawei.Lpv2;

namespace EarCtrl.Agents.Huawei.Tests;

public class GoldenSampleTests
{
    // test_battery.py 定义的三组字节
    private const string GetBatteryRqHex = "5A0009000108010002000300FBB9";
    private const string GetBatteryRespBaseHex = "5A0014000108010140020310203003030001000402140A1461";
    private const string GetBatteryRespLegacyHex = "5A0009000108010140030100ED2E";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void BatteryReadRequest_MatchesGoldenBytes()
    {
        var pkg = HuaweiCommands.BatteryReadRequest();
        Assert.Equal(GetBatteryRqHex, Convert.ToHexString(pkg.ToBytes()));
    }

    [Fact]
    public void GoldenBatteryRequest_ParsesBackToSameCommand()
    {
        var pkg = HuaweiSppPackage.FromBytes(Hex(GetBatteryRqHex), validateChecksum: true);
        Assert.Equal("0108", Convert.ToHexString(pkg.CommandId));
        Assert.Equal(3, pkg.Parameters.Count);
        Assert.Empty(pkg.Parameters[1]);
        Assert.Empty(pkg.Parameters[2]);
        Assert.Empty(pkg.Parameters[3]);
    }

    [Fact]
    public void GoldenBatteryResponse_PassesCrcValidation()
    {
        // 参考实现产物自带合法 CRC：能通过校验即证明我们的 CRC16-XMODEM 与参考实现一致
        var ex = Record.Exception(() =>
            HuaweiSppPackage.FromBytes(Hex(GetBatteryRespBaseHex), validateChecksum: true));
        Assert.Null(ex);

        var ex2 = Record.Exception(() =>
            HuaweiSppPackage.FromBytes(Hex(GetBatteryRespLegacyHex), validateChecksum: true));
        Assert.Null(ex2);
    }

    [Fact]
    public void GoldenBatteryResponse_ParsesLevels()
    {
        var pkg = HuaweiSppPackage.FromBytes(Hex(GetBatteryRespBaseHex));
        var (global, left, right, caseLevel, charging, caseCharging) = HuaweiCommands.ParseBattery(pkg);

        Assert.Equal(0x40, global);
        Assert.Equal(0x10, left);
        Assert.Equal(0x20, right);
        Assert.Equal(0x30, caseLevel);
        Assert.True(charging);
        // param3 = 00 01 00 → 右耳充电中，充电盒（第三字节）未充电
        Assert.False(caseCharging);
    }

    [Fact]
    public void GoldenBatteryResponseLegacy_HasNoTwsLevels()
    {
        var pkg = HuaweiSppPackage.FromBytes(Hex(GetBatteryRespLegacyHex));
        var (global, left, right, caseLevel, charging, caseCharging) = HuaweiCommands.ParseBattery(pkg);

        Assert.Equal(0x40, global);
        Assert.Null(left);
        Assert.Null(right);
        Assert.Null(caseLevel);
        Assert.False(charging);
        Assert.Null(caseCharging);
    }

    [Fact]
    public void ReEncode_GoldenBatteryResponse_IsByteIdentical()
    {
        var golden = Hex(GetBatteryRespBaseHex);
        var pkg = HuaweiSppPackage.FromBytes(golden);
        Assert.Equal(GetBatteryRespBaseHex, Convert.ToHexString(pkg.ToBytes()));
    }
}
