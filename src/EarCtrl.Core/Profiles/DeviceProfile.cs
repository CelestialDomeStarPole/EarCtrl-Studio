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

    /// <summary>
    /// 降噪模式下可选等级（值为 App/协议 level tag：comfort=轻度、normal=均衡、ultra=深度、dynamic=动态）。
    /// 空列表 = 设备不支持等级调节（页面隐藏等级行）。这是等级的**唯一权威声明**。
    /// </summary>
    [JsonPropertyName("ancLevels")]
    public List<string> AncLevels { get; set; } = [];

    /// <summary>支持防风噪开关（华为 2B B4 子功能总线 0x1D；官方智慧音频 App 降噪页实装，OpenFreebuds 未支持）。</summary>
    [JsonPropertyName("ancWindNoise")]
    public bool AncWindNoise { get; set; }

    [JsonPropertyName("equalizer")]
    public EqualizerCaps Equalizer { get; set; } = new();

    [JsonPropertyName("lowLatency")]
    public bool LowLatency { get; set; }

    [JsonPropertyName("holdAction")]
    public HoldActionCaps HoldAction { get; set; } = new();

    /// <summary>双击动作（读 01 20 / 写 01 1F）。</summary>
    [JsonPropertyName("doubleTap")]
    public GestureCaps DoubleTap { get; set; } = new();

    /// <summary>三击动作（读 01 26 / 写 01 25）。</summary>
    [JsonPropertyName("tripleTap")]
    public GestureCaps TripleTap { get; set; } = new();

    /// <summary>连击（五下）动作（2B B4 子功能 0x14）。</summary>
    [JsonPropertyName("multiTap")]
    public GestureCaps MultiTap { get; set; } = new();

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
    /// <summary>左耳可配置长按。</summary>
    [JsonPropertyName("left")]
    public bool Left { get; set; }

    /// <summary>右耳可配置长按。</summary>
    [JsonPropertyName("right")]
    public bool Right { get; set; }

    /// <summary>长按动作语义 tag（off/anc-cycle/voice-assistant/volume-up/volume-down）。</summary>
    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    /// <summary>通话中长按语义 tag（off/answer）；空列表 = 不支持。</summary>
    [JsonPropertyName("inCallOptions")]
    public List<string> InCallOptions { get; set; } = [];

    /// <summary>长按切换降噪时的循环模式选项（1..4；空列表 = 不暴露该设置）。</summary>
    [JsonPropertyName("ancCycleOptions")]
    public List<int> AncCycleOptions { get; set; } = [];
}

/// <summary>点击类手势能力（双击 / 三击 / 连击）：左右耳可配置 + 动作语义 tag 列表。</summary>
public sealed class GestureCaps
{
    [JsonPropertyName("left")]
    public bool Left { get; set; }

    [JsonPropertyName("right")]
    public bool Right { get; set; }

    /// <summary>动作语义 tag 列表（如 off/play-pause/next/prev）。</summary>
    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];

    /// <summary>通话中动作 tag（空列表 = 不支持在通话中配置）。</summary>
    [JsonPropertyName("inCallOptions")]
    public List<string> InCallOptions { get; set; } = [];
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
