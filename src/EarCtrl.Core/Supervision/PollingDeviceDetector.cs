using System.Collections.Concurrent;
using EarCtrl.Core.Profiles;

namespace EarCtrl.Core.Supervision;

/// <summary>
/// 设备探测器抽象。实现负责把「设备出现/离开」翻译成事件；
/// SPP 设备用连接状态轮询，BLE 设备用广播监听（§3.1-A 双通道）。
/// </summary>
public interface IDeviceDetector
{
    /// <summary>设备出现（已通过去抖确认），参数：设备名、蓝牙地址。</summary>
    event Action<string, ulong>? DeviceAppeared;

    /// <summary>设备离开。</summary>
    event Action<string>? DeviceDisappeared;

    void Start(IReadOnlyList<DeviceProfile> profiles);
    Task StopAsync();
}

/// <summary>
/// 经典蓝牙轮询探测器：按固定间隔枚举已配对设备连接状态，名称匹配 Profile；
/// 命中需连续两次（去抖）才报 Appeared。
/// </summary>
public sealed class PollingDeviceDetector : IDeviceDetector, IDisposable
{
    private readonly ConcurrentDictionary<string, int> _hitStreak = new();
    private readonly HashSet<string> _present = [];
    private readonly object _gate = new();
    private Timer? _timer;
    private IReadOnlyList<DeviceProfile> _profiles = [];
    private int _intervalMs = 1500;
    private readonly int _debounceTicks = 2;

    public event Action<string, ulong>? DeviceAppeared;
    public event Action<string>? DeviceDisappeared;

    public void Start(IReadOnlyList<DeviceProfile> profiles)
    {
        _profiles = profiles;
        _timer ??= new Timer(async _ => await TickAsync(), null, 0, _intervalMs);
    }

    public void ConfigureInterval(int intervalMs)
    {
        _intervalMs = intervalMs;
        _timer?.Change(0, intervalMs);
    }

    private async Task TickAsync()
    {
        try
        {
            var selector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
            var matched = new HashSet<string>();

            foreach (var d in devices)
            {
                var dev = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(d.Id);
                if (dev is null) continue;

                var profile = DeviceProfileRegistry.MatchByName(_profiles, dev.Name);
                if (profile is null) continue;

                var isOnline = dev.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected;
                lock (_gate)
                {
                    if (!isOnline) continue;
                    matched.Add(dev.Name);
                    var streak = _hitStreak.AddOrUpdate(dev.Name, 1, (_, v) => v + 1);
                    if (!_present.Contains(dev.Name) && streak >= _debounceTicks)
                    {
                        _present.Add(dev.Name);
                        DeviceAppeared?.Invoke(dev.Name, dev.BluetoothAddress);
                    }
                }
            }

            lock (_gate)
            {
                foreach (var name in _present.Where(n => !matched.Contains(n)).ToList())
                {
                    _present.Remove(name);
                    _hitStreak.TryRemove(name, out _);
                    DeviceDisappeared?.Invoke(name);
                }
            }
        }
        catch
        {
            // 蓝牙不可用/适配器拔插：本轮跳过，下个 tick 重试（§3.1-A 蓝牙关闭场景）
        }
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
