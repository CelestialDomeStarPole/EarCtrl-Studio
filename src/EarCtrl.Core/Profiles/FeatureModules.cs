namespace EarCtrl.Core.Profiles;

/// <summary>
/// 页内控件模块标识（module id）。
/// 约定：导航项不裁剪，仅页面内的控件模块按设备能力显示/隐藏；
/// 命名格式「页面.模块(.子模块)」，全小写驼峰。新增模块时同时在此登记并在 <see cref="FeatureGate"/> 映射能力。
/// </summary>
public static class FeatureModules
{
    // ---- 设备主页 ----
    public const string DashboardBatteryLeft = "dashboard.battery.left";
    public const string DashboardBatteryRight = "dashboard.battery.right";
    public const string DashboardBatteryCase = "dashboard.battery.case";
    public const string DashboardCaseCharging = "dashboard.battery.caseCharging";
    public const string DashboardVoltage = "dashboard.voltage";
    public const string DashboardTemperature = "dashboard.temperature";

    // ---- 降噪页 ----
    public const string AncModeCancellation = "anc.mode.cancellation";
    public const string AncModeAwareness = "anc.mode.awareness";
    public const string AncModeNormal = "anc.mode.normal";
    public const string AncLevel = "anc.level";
    public const string AncLevelDynamic = "anc.level.dynamic";
    public const string AncLevelComfort = "anc.level.comfort";
    public const string AncLevelNormal = "anc.level.normal";
    public const string AncLevelUltra = "anc.level.ultra";
    public const string AncWindNoise = "anc.windNoise";

    // ---- 以下为 M4 页面预留（模块实现时直接接入同一机制）----
    public const string EqualizerPresets = "equalizer.presets";
    public const string EqualizerCustom = "equalizer.custom";
    public const string ControlsHoldAction = "controls.holdAction";
    public const string ControlsDualConnect = "controls.dualConnect";
    public const string ControlsFindMy = "controls.findMy";
    public const string ControlsWearDetect = "controls.wearDetect";
    public const string ControlsLdac = "controls.ldac";
}

/// <summary>页内模块的可用性判定结果。</summary>
public enum FeatureAvailability
{
    /// <summary>能力未知（未连接 / 无当前设备）：**显示但虚化禁用**。</summary>
    Unknown,

    /// <summary>设备支持：正常显示；是否可交互仍由页面自身的连接态/pending 逻辑决定。</summary>
    Available,

    /// <summary>设备不支持：**直接隐藏**。</summary>
    Unavailable,
}

/// <summary>
/// 能力门：以 Profile 静态声明为**唯一权威**（用户 2026-10-27 定调，不做 Agent 运行时覆盖），
/// 按当前设备的 capabilities 判定页内模块可用性。
/// </summary>
public static class FeatureGate
{
    /// <param name="moduleId">见 <see cref="FeatureModules"/>。</param>
    /// <param name="profile">当前设备 Profile（无当前设备时为 null）。</param>
    /// <param name="connected">链路是否已就绪（未连接时一律 Unknown：先全部显示虚化，连接后收敛）。</param>
    public static FeatureAvailability Evaluate(string moduleId, DeviceProfile? profile, bool connected)
    {
        if (!connected || profile is null)
        {
            return FeatureAvailability.Unknown;
        }

        var caps = profile.Capabilities;
        return moduleId switch
        {
            FeatureModules.DashboardBatteryLeft => Flag(caps.Battery.Left),
            FeatureModules.DashboardBatteryRight => Flag(caps.Battery.Right),
            FeatureModules.DashboardBatteryCase => Flag(caps.Battery.Case),
            FeatureModules.DashboardCaseCharging => Flag(caps.Battery.Case),
            FeatureModules.DashboardVoltage => Flag(caps.Voltage),
            FeatureModules.DashboardTemperature => Flag(caps.Temperature),

            FeatureModules.AncModeCancellation => Flag(caps.AncModes.Contains("cancellation")),
            FeatureModules.AncModeAwareness => Flag(caps.AncModes.Contains("awareness")),
            FeatureModules.AncModeNormal => Flag(caps.AncModes.Contains("normal")),
            FeatureModules.AncLevel => Flag(caps.AncLevels.Count > 0),
            FeatureModules.AncLevelDynamic => Flag(caps.AncLevels.Contains("dynamic")),
            FeatureModules.AncLevelComfort => Flag(caps.AncLevels.Contains("comfort")),
            FeatureModules.AncLevelNormal => Flag(caps.AncLevels.Contains("normal")),
            FeatureModules.AncLevelUltra => Flag(caps.AncLevels.Contains("ultra")),
            FeatureModules.AncWindNoise => Flag(caps.AncWindNoise),

            FeatureModules.EqualizerPresets => Flag(caps.Equalizer.Presets.Count > 0),
            FeatureModules.EqualizerCustom => Flag(caps.Equalizer.Custom),
            FeatureModules.ControlsHoldAction => Flag(caps.HoldAction.Options.Count > 0),
            FeatureModules.ControlsDualConnect => Flag(caps.DualConnect),
            FeatureModules.ControlsFindMy => Flag(caps.FindMy),
            FeatureModules.ControlsWearDetect => Flag(caps.WearDetect),
            FeatureModules.ControlsLdac => Flag(caps.Ldac),

            // 未登记的模块按「未知」处理（显示虚化），避免漏配能力导致控件直接消失
            _ => FeatureAvailability.Unknown,
        };
    }

    private static FeatureAvailability Flag(bool supported) =>
        supported ? FeatureAvailability.Available : FeatureAvailability.Unavailable;
}
