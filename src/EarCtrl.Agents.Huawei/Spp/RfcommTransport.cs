// SPDX-License-Identifier: GPL-3.0-only
// SPP/RFCOMM 传输（借鉴 OpenFreebuds _BluetoothSocketTransport：直连 (地址, 信道)，WinRT 原生实现）。
// Windows 实现走 WinRT RfcommDeviceService(SerialPort) + StreamSocket；
// 注意：OpenFreebuds 硬编码信道 1（buds_se_4.py `_spp_service_port = 1`），
// WinRT 无法直接按信道号连，改为通过 SPP 服务 SDP 记录解析 ConnectionServiceName。

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace EarCtrl.Agents.Huawei.Spp;

public sealed class RfcommTransport : IDisposable
{
    private StreamSocket? _socket;
    private DataReader? _reader;
    private DataWriter? _writer;

    public Stream TransportStream { get; private set; } = Stream.Null;

    /// <summary>连接耳机 SPP 服务。SE 4 ANC 使用 SPP（00001101），信道 1。</summary>
    public async Task ConnectAsync(ulong bluetoothAddress, int channel = 1)
    {
        var device = await BluetoothDevice.FromBluetoothAddressAsync(bluetoothAddress)
                     ?? throw new InvalidOperationException($"未找到蓝牙设备 {bluetoothAddress:X12}");

        var resolved = await ResolveSerialPortServiceAsync(device, channel);
        if (resolved is null)
        {
            throw new InvalidOperationException("设备未暴露 SPP (00001101) 服务记录");
        }
        var service = resolved.Service;
        var allServices = resolved.All;

        Console.Error.WriteLine(
            $"[rfcomm] 候选服务 {allServices.Count} 个，选中 ConnectionServiceName={service.ConnectionServiceName}");
        foreach (var s in allServices)
        {
            Console.Error.WriteLine($"[rfcomm]   - id={s.ServiceId.Uuid} name={s.ConnectionServiceName}");
        }

        _socket = new StreamSocket();
        await _socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
            SocketProtectionLevel.PlainSocket);

        _reader = new DataReader(_socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        _writer = new DataWriter(_socket.OutputStream);

        TransportStream = new DuplexStreamAdapter(_reader, _writer);
    }

    private sealed record ResolvedService(RfcommDeviceService Service, IReadOnlyList<RfcommDeviceService> All);

    private static async Task<ResolvedService?> ResolveSerialPortServiceAsync(
        BluetoothDevice device, int channel)
    {
        // 优先取缓存记录（耳机连着 A2DP 时无配对流程，缓存即可用）
        var result = await device.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
        var service = Pick(result, channel);
        if (service != null) return new ResolvedService(service, result.Services);

        // 缓存未命中则现场查询 SDP
        var fresh = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
        service = Pick(fresh, channel);
        return service != null ? new ResolvedService(service, fresh.Services) : null;
    }

    private static RfcommDeviceService? Pick(RfcommDeviceServicesResult result, int channel)
    {
        if (result.Services.Count == 0) return null;
        foreach (var s in result.Services)
        {
            if (s.ServiceId.AsString() == RfcommServiceId.SerialPort.AsString())
            {
                return s;
            }
        }
        // SPP 服务记录缺失但信道已知时，取第一个服务兜底（与 OpenFreebuds 硬编码信道等价场景）
        return channel == 1 ? result.Services[0] : null;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _socket?.Dispose();
        _reader = null;
        _writer = null;
        _socket = null;
        TransportStream = Stream.Null;
    }

    /// <summary>把 WinRT DataReader/Writer 适配成 .NET Stream（帧收发循环用）。</summary>
    private sealed class DuplexStreamAdapter(DataReader reader, DataWriter writer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var loaded = await reader.LoadAsync((uint)count).AsTask(ct);
            if (loaded == 0) return 0;
            var n = (int)Math.Min(loaded, count);
            var bytes = reader.ReadBuffer((uint)n).ToArray();
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            return bytes.Length;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var data = new byte[count];
            Array.Copy(buffer, offset, data, 0, count);
            writer.WriteBytes(data);
            await writer.StoreAsync().AsTask(ct);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
