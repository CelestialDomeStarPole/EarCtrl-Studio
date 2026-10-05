using EarCtrl.Core.Profiles;

namespace EarCtrl.Core.Supervision;

/// <summary>扫描结果：已配对设备 + 命中的 Profile。</summary>
public sealed record PairedDeviceInfo(string Name, ulong Address, DeviceProfile? Profile);

/// <summary>
/// 已配对蓝牙设备枚举（添加设备对话框 / 手动连接兜底的地址查找）。
/// 与 PollingDeviceDetector 同一选择器：经典蓝牙已配对设备。
/// </summary>
public static class PairedDeviceScanner
{
    public static async Task<IReadOnlyList<PairedDeviceInfo>> ScanAsync(IReadOnlyList<DeviceProfile> profiles)
    {
        var selector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
        var result = new List<PairedDeviceInfo>();

        foreach (var d in devices)
        {
            var dev = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(d.Id);
            if (dev is null) continue;
            result.Add(new PairedDeviceInfo(dev.Name, dev.BluetoothAddress,
                DeviceProfileRegistry.MatchByName(profiles, dev.Name)));
        }
        return result;
    }
}
