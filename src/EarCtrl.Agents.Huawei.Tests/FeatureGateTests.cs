// SPDX-License-Identifier: GPL-3.0-only
// 能力门单测：Profile 为唯一权威；未连接 = Unknown（显示虚化），连接后按能力 Available/Unavailable（隐藏）。

using EarCtrl.Core.Profiles;

namespace EarCtrl.Agents.Huawei.Tests;

public class FeatureGateTests
{
    private static DeviceProfile Profile(Action<Capabilities> configure)
    {
        var profile = new DeviceProfile { Brand = "test" };
        configure(profile.Capabilities);
        return profile;
    }

    [Fact]
    public void Unconnected_IsAlwaysUnknown()
    {
        var profile = Profile(c =>
        {
            c.Battery.Left = true;
            c.AncWindNoise = true;
        });

        Assert.Equal(FeatureAvailability.Unknown,
            FeatureGate.Evaluate(FeatureModules.DashboardBatteryLeft, profile, connected: false));
        Assert.Equal(FeatureAvailability.Unknown,
            FeatureGate.Evaluate(FeatureModules.AncWindNoise, profile, connected: false));
    }

    [Fact]
    public void NoCurrentDevice_IsUnknown()
    {
        Assert.Equal(FeatureAvailability.Unknown,
            FeatureGate.Evaluate(FeatureModules.DashboardBatteryLeft, profile: null, connected: true));
    }

    [Fact]
    public void Connected_UnsupportedModuleIsUnavailable()
    {
        var profile = Profile(c =>
        {
            c.Battery.Left = true;
            c.Battery.Case = false;
        });

        Assert.Equal(FeatureAvailability.Available,
            FeatureGate.Evaluate(FeatureModules.DashboardBatteryLeft, profile, connected: true));
        Assert.Equal(FeatureAvailability.Unavailable,
            FeatureGate.Evaluate(FeatureModules.DashboardBatteryCase, profile, connected: true));
    }

    [Fact]
    public void AncLevels_DynamicOptionRequiresDynamicInList()
    {
        var withDynamic = Profile(c => c.AncLevels.AddRange(["comfort", "normal", "ultra", "dynamic"]));
        Assert.Equal(FeatureAvailability.Available,
            FeatureGate.Evaluate(FeatureModules.AncLevelDynamic, withDynamic, connected: true));

        var withoutDynamic = Profile(c => c.AncLevels.AddRange(["comfort", "normal", "ultra"]));
        Assert.Equal(FeatureAvailability.Unavailable,
            FeatureGate.Evaluate(FeatureModules.AncLevelDynamic, withoutDynamic, connected: true));
    }

    [Fact]
    public void AncLevels_PerLevelModulesFollowList()
    {
        var profile = Profile(c => c.AncLevels.AddRange(["comfort", "dynamic"]));

        Assert.Equal(FeatureAvailability.Available,
            FeatureGate.Evaluate(FeatureModules.AncLevelComfort, profile, connected: true));
        Assert.Equal(FeatureAvailability.Unavailable,
            FeatureGate.Evaluate(FeatureModules.AncLevelNormal, profile, connected: true));
        Assert.Equal(FeatureAvailability.Unavailable,
            FeatureGate.Evaluate(FeatureModules.AncLevelUltra, profile, connected: true));
        Assert.Equal(FeatureAvailability.Available,
            FeatureGate.Evaluate(FeatureModules.AncLevelDynamic, profile, connected: true));
    }

    [Fact]
    public void EmptyAncLevels_HidesLevelRow()
    {
        var profile = Profile(_ => { });
        Assert.Equal(FeatureAvailability.Unavailable,
            FeatureGate.Evaluate(FeatureModules.AncLevel, profile, connected: true));
    }

    [Fact]
    public void UnregisteredModuleId_IsUnknown_SoItStaysVisible()
    {
        var profile = Profile(_ => { });
        Assert.Equal(FeatureAvailability.Unknown,
            FeatureGate.Evaluate("some.future.module", profile, connected: true));
    }
}
