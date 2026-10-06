using System.Text.Json;

namespace EarCtrl.Core.Config;

/// <summary>
/// 应用全局配置（MIT 侧）。所有超时/时间常量亦集中于此，允许 config.json 覆盖。
/// 详见《软件实现计划》§3.1-F。
/// </summary>
public sealed class AppConfig
{
    // 外观
    public string Theme { get; set; } = "FollowSystem";   // FollowSystem | Dark | Light
    public string Language { get; set; } = "zh-CN";       // zh-CN | en-US
    public bool DevToolsVisible { get; set; } = false;

    // 状态轮询（华为 Agent）：0=关闭，1-5=秒。轮询驱动在仓/佩戴检测；电量另有耳机事件推送
    public int PollIntervalSec { get; set; } = 1;

    // 已添加设备（brand 列表，K10 设备区；默认含随附华为 Profile，首启动即用）
    public List<string> AddedDevices { get; set; } = ["huawei"];

    // Agent 生命周期常量（秒；§3.1 常量表，允许覆盖）
    public int MatchDebounceMs { get; set; } = 1500;      // 广播匹配去抖
    public int SilentPollIntervalSec { get; set; } = 10;  // 连接后广播停播时的兜底轮询
    public int DeviceAwayTimeoutSec { get; set; } = 180;  // 设备离开 → Agent 回收
    public int HeartbeatSilenceSec { get; set; } = 30;    // Agent 静默期心跳间隔
    public int HeartbeatTimeoutSec { get; set; } = 90;    // 主程序判定僵死（3×心跳）
    public int ShutdownGraceSec { get; set; } = 3;        // shutdown 后等待退出
    public int OrphanGraceSec { get; set; } = 30;         // Agent 检测主程序死亡后的等待
    public int AgentConnectTimeoutSec { get; set; } = 15; // 主程序连 Agent 管道等待（Agent 冷启动含 JIT+单实例探测）
    public int RestartWindowSec { get; set; } = 300;      // 熔断统计窗口
    public int RestartLimit { get; set; } = 3;            // 窗口内重启次数上限
    public int CircuitCooldownSec { get; set; } = 60;     // 熔断冷却
}

/// <summary>
/// 配置读写：安装版 → %LOCALAPPDATA%\EarCtrl\config.json；
/// portable 版 → exe 同目录存在 portable.flag 时写入 .\data\（零注册表）。
/// </summary>
public static class ConfigStore
{
    private static string? _dir;

    public static string ResolveDir()
    {
        if (_dir != null) return _dir;
        var portableFlag = Path.Combine(AppContext.BaseDirectory, "portable.flag");
        _dir = File.Exists(portableFlag)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EarCtrl");
        Directory.CreateDirectory(_dir);
        return _dir;
    }

    public static string ResolvePath() => Path.Combine(ResolveDir(), "config.json");

    public static AppConfig Load()
    {
        var path = ResolvePath();
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch (Exception)
        {
            // 配置损坏：备份为 .bak，以默认配置启动（§3.1-E）
            try { File.Move(path, path + ".bak", overwrite: true); } catch { /* 忽略备份失败 */ }
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        var path = ResolvePath();
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
