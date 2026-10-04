// SPDX-License-Identifier: GPL-3.0-only
// 工程链路冒烟测试。

namespace EarCtrl.Agents.Huawei.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProject_WiredCorrectly()
    {
        // 能解析被测程序集类型即通过（AgentOptions 为 internal，经 InternalsVisibleTo 可见）
        var agentAssemblyName = typeof(AgentOptions).Assembly.GetName().Name;
        Assert.Equal("EarCtrl.Agent.Huawei", agentAssemblyName);
    }

    [Fact]
    public void AgentOptions_ParsesCliArgs()
    {
        var opts = AgentOptions.Parse(
        [
            "--address", "AA:BB:CC:DD:EE:FF",
            "--verbose",
            "anc", "set", "cancellation", "ultra",
        ]);

        Assert.Equal("AA:BB:CC:DD:EE:FF", opts.Address);
        Assert.True(opts.Verbose);
        Assert.Equal("anc", opts.Command);
        Assert.Equal(new[] { "set", "cancellation", "ultra" }, opts.Arguments);
    }
}
