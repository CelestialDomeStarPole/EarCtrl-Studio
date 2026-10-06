// SPDX-License-Identifier: GPL-3.0-only
// CRC16-XMODEM（借鉴 OpenFreebuds driver/huawei/utils.py crc16_xmodem；poly=0x1021, init=0，C# 查找表实现）。

namespace EarCtrl.Agents.Huawei.Lpv2;

public static class Crc16
{
    public static byte[] Xmodem(ReadOnlySpan<byte> data)
    {
        ushort s = 0;
        foreach (var b in data)
        {
            s = (ushort)((s << 8) ^ Table[((s >> 8) ^ b) & 0xFF]);
        }
        return [(byte)(s >> 8), (byte)(s & 0xFF)];
    }

    // 256 项查找表，由多项式 0x1021 生成（与 OpenFreebuds 源码中的硬编码表等价）
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        const ushort poly = 0x1021;
        for (ushort i = 0; i < 256; i++)
        {
            ushort crc = 0;
            var b = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)(((b ^ crc) & 0x8000) != 0 ? (crc << 1) ^ poly : crc << 1);
                b <<= 1;
            }
            table[i] = crc;
        }
        // 自校验：与 OpenFreebuds 源码硬编码表抽查一致（4129=0x1021, 8258=0x2042, -32504→0x8108）
        if (table[0] != 0 || table[1] != 0x1021 || table[2] != 0x2042 || table[8] != 0x8108)
        {
            throw new InvalidOperationException("CRC16-XMODEM table mismatch with reference implementation");
        }
        return table;
    }
}
