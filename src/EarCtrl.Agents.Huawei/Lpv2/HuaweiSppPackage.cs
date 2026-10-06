// SPDX-License-Identifier: GPL-3.0-only
// 华为 SPP 包帧编解码（借鉴 OpenFreebuds driver/huawei/package.py HuaweiSppPackage 的帧格式实现）。
//
// 帧结构：
//   'Z'(0x5A) | len(2, 大端, = body长度+1) | 0x00 | body | crc16_xmodem(2, 大端)
//   body = command_id(2) + { param_type(1) + param_len(1) + value }...
// 注意：与 Python 版一致，解析循环条件为 position < len+3，允许截断尾参（镜像实现，勿"修复"）。

namespace EarCtrl.Agents.Huawei.Lpv2;

public sealed class HuaweiSppPackage
{
    public const byte HeaderByte = 0x5A;

    public byte[] CommandId { get; }
    public byte[] ResponseId { get; set; } = [];
    public SortedDictionary<int, byte[]> Parameters { get; } = new();

    public HuaweiSppPackage(byte[] commandId)
    {
        CommandId = commandId.Length == 2 ? commandId : throw new ArgumentException("commandId 必须为 2 字节");
    }

    // ---- 构造辅助（对应 Python 静态方法） ----

    /// <summary>change_rq：写请求，期望同名命令响应。</summary>
    public static HuaweiSppPackage ChangeRequest(byte[] cmd, params (int Type, byte[] Value)[] parameters) =>
        Build(cmd, cmd, parameters);

    /// <summary>change_rq_nowait：写请求，不等待响应。</summary>
    public static HuaweiSppPackage ChangeRequestNoWait(byte[] cmd, params (int Type, byte[] Value)[] parameters) =>
        Build(cmd, null, parameters);

    /// <summary>read_rq：读请求，参数只含类型（空值）。</summary>
    public static HuaweiSppPackage ReadRequest(byte[] cmd, params int[] paramTypes)
    {
        var pkg = new HuaweiSppPackage(cmd) { ResponseId = cmd };
        foreach (var t in paramTypes)
        {
            pkg.Parameters[t] = [];
        }
        return pkg;
    }

    private static HuaweiSppPackage Build(byte[] cmd, byte[]? responseId, (int Type, byte[] Value)[] parameters)
    {
        var pkg = new HuaweiSppPackage(cmd);
        if (responseId != null) pkg.ResponseId = responseId;
        foreach (var (type, value) in parameters)
        {
            pkg.Parameters[type] = value;
        }
        return pkg;
    }

    // ---- 参数访问 ----

    public byte[] FindParam(params int[] types)
    {
        foreach (var t in types)
        {
            if (Parameters.TryGetValue(t, out var v))
            {
                return v;
            }
        }
        return [];
    }

    // ---- 序列化 ----

    public byte[] ToBytes()
    {
        var body = new List<byte> { CommandId[0], CommandId[1] };
        foreach (var (type, value) in Parameters)
        {
            body.Add((byte)type);
            body.Add((byte)value.Length);
            body.AddRange(value);
        }

        var result = new List<byte> { HeaderByte };
        var len = body.Count + 1;
        result.Add((byte)(len >> 8));
        result.Add((byte)(len & 0xFF));
        result.Add(0x00);
        result.AddRange(body);
        result.AddRange(Crc16.Xmodem(result.ToArray()));
        return result.ToArray();
    }

    // ---- 反序列化 ----

    public static HuaweiSppPackage FromBytes(ReadOnlySpan<byte> data, bool validateChecksum = false)
    {
        if (data.Length < 7 || data[0] != HeaderByte || data[3] != 0)
        {
            throw new ArgumentException("非华为 SPP 包帧（帧头/保留位校验失败）");
        }

        if (validateChecksum)
        {
            var expected = Crc16.Xmodem(data[..^2]);
            if (!expected.AsSpan().SequenceEqual(data[^2..]))
            {
                throw new InvalidOperationException(
                    $"CRC 校验失败: 期望 {Convert.ToHexString(expected)} 实际 {Convert.ToHexString(data[^2..])}");
            }
        }

        var length = (data[1] << 8) | data[2];
        var commandId = data[4..6].ToArray();
        var package = new HuaweiSppPackage(commandId);

        var position = 6;
        while (position < length + 3 && position + 2 <= data.Length)
        {
            var pType = data[position];
            var pLen = data[position + 1];
            if (position + 2 + pLen > data.Length)
            {
                // 与 Python 行为一致：数据不足时按可用部分截断
                package.Parameters[pType] = data[(position + 2)..].ToArray();
                break;
            }
            package.Parameters[pType] = data[(position + 2)..(position + 2 + pLen)].ToArray();
            position += pLen + 2;
        }

        return package;
    }

    public override string ToString()
    {
        var parts = new List<string> { $"command={Convert.ToHexString(CommandId)}" };
        parts.AddRange(Parameters.Select(kv => $"param_{kv.Key}={Convert.ToHexString(kv.Value)}"));
        return string.Join(", ", parts);
    }
}
