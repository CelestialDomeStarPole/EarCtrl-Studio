using System.Text.Json;
using System.Text.Json.Serialization;

namespace EarCtrl.Core.Profiles;

/// <summary>
/// 设备描述文件模型（profiles/*.device.json）。
/// AgentSupervisor 靠它做「探测匹配 → 选 Agent → 起管道」；能力矩阵决定 UI 显示哪些功能。
/// </summary>
public sealed class DeviceProfile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("brand")]
    public string Brand { get; set; } = "";

    /// <summary>设备形态：earbuds（入耳式，默认）| overhead（头戴式），决定设备主页卡片布局。</summary>
    [JsonPropertyName("formFactor")]
    public string FormFactor { get; set; } = "earbuds";

    [JsonPropertyName("agentExe")]
    public string AgentExe { get; set; } = "";

    [JsonPropertyName("pipeName")]
    public string PipeName { get; set; } = "";

    [JsonPropertyName("model")]
    public ModelInfo Model { get; set; } = new();

    [JsonPropertyName("matchRules")]
    public MatchRules Rules { get; set; } = new();

    /// <summary>协议通道：spp（经典蓝牙 RFCOMM，探测靠连接状态轮询）或 ble（探测靠广播监听）。</summary>
    [JsonPropertyName("transport")]
    public string Transport { get; set; } = "spp";

    [JsonPropertyName("capabilities")]
    public Capabilities Capabilities { get; set; } = new();

    /// <summary>来源文件路径（LoadAll 填充；进程拉起时传给 Agent --profile）。</summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }
}

public sealed class ModelInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("deviceModel")]
    public string DeviceModel { get; set; } = "";
}

public sealed class MatchRules
{
    [JsonPropertyName("namePrefixes")]
    public List<string> NamePrefixes { get; set; } = [];

    [JsonPropertyName("manufacturerIds")]
    public List<int> ManufacturerIds { get; set; } = [];

    [JsonPropertyName("serviceUuids")]
    public List<string> ServiceUuids { get; set; } = [];
}

public sealed class Capabilities
{
    [JsonPropertyName("battery")]
    public BatteryCaps Battery { get; set; } = new();

    [JsonPropertyName("ancModes")]
    public List<string> AncModes { get; set; } = [];

    /// <summary>降噪模式下支持「动态」等级（对照 OpenFreebuds per_model 的 w_cancel_dynamic 名单：SE 4/5i/6i/Pro/Pro 3/Pro 5/Studio/Lace Pro 2）。</summary>
    [JsonPropertyName("ancDynamicLevel")]
    public bool AncDynamicLevel { get; set; }

    /// <summary>支持防风噪开关（华为 2B B4 子功能总线 0x1D；官方智慧音频 App 降噪页实装，OpenFreebuds 未支持）。</summary>
    [JsonPropertyName("ancWindNoise")]
    public bool AncWindNoise { get; set; }

    [JsonPropertyName("equalizer")]
    public EqualizerCaps Equalizer { get; set; } = new();

    [JsonPropertyName("lowLatency")]
    public bool LowLatency { get; set; }

    [JsonPropertyName("holdAction")]
    public HoldActionCaps HoldAction { get; set; } = new();

    [JsonPropertyName("dualConnect")]
    public bool DualConnect { get; set; }

    [JsonPropertyName("findMy")]
    public bool FindMy { get; set; }

    [JsonPropertyName("wearDetect")]
    public bool WearDetect { get; set; }

    /// <summary>支持电压上报。</summary>
    [JsonPropertyName("voltage")]
    public bool Voltage { get; set; }

    /// <summary>支持温度上报。</summary>
    [JsonPropertyName("temperature")]
    public bool Temperature { get; set; }

    [JsonPropertyName("ldac")]
    public bool Ldac { get; set; }
}

public sealed class BatteryCaps
{
    [JsonPropertyName("left")]
    public bool Left { get; set; }

    [JsonPropertyName("right")]
    public bool Right { get; set; }

    [JsonPropertyName("case")]
    public bool Case { get; set; }
}

public sealed class EqualizerCaps
{
    [JsonPropertyName("presets")]
    public List<string> Presets { get; set; } = [];

    [JsonPropertyName("custom")]
    public bool Custom { get; set; }
}

public sealed class HoldActionCaps
{
    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];
}

/// <summary>Profile 注册表：加载 profiles 目录并做匹配。</summary>
public static class DeviceProfileRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultProfilesDir =>
        Path.Combine(AppContext.BaseDirectory, "profiles");

    public static IReadOnlyList<DeviceProfile> LoadAll(string? dir = null)
    {
        var profiles = new List<DeviceProfile>();
        var path = dir ?? DefaultProfilesDir;
        if (!Directory.Exists(path)) return profiles;

        foreach (var file in Directory.EnumerateFiles(path, "*.device.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), JsonOptions);
                if (profile != null && !string.IsNullOrEmpty(profile.Brand))
                {
                    profile.SourcePath = Path.GetFullPath(file);
                    profiles.Add(profile);
                }
            }
            catch (JsonException)
            {
                // 单个 Profile 损坏不阻塞其他设备
            }
        }
        return profiles;
    }

    /// <summary>
    /// 按设备名匹配（三级：服务 UUID > 厂商 ID > 名称前缀；名称匹配是 SPP 设备的主要路径）。
    /// 返回命中的 Profile，未命中返回 null。
    /// </summary>
    public static DeviceProfile? MatchByName(IReadOnlyList<DeviceProfile> profiles, string deviceName)
    {
        foreach (var p in profiles)
        {
            foreach (var prefix in p.Rules.NamePrefixes)
            {
                if (!string.IsNullOrEmpty(prefix) &&
                    deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
        }
        return null;
    }
}
