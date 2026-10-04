// EarCtrl.Agents.Huawei.Tests — 华为 Agent 协议层单测（黄金样例）
// SPDX-License-Identifier: GPL-3.0-only
//
// M1 起，此处将用「抓包 hex = 黄金样例」覆盖 Lpv2 帧编解码与 HuaweiCommands。
// M0 仅保留冒烟测试验证工程链路。

namespace EarCtrl.Agents.Huawei.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProject_WiredCorrectly()
    {
        // 工程引用链路冒烟：能解析被测程序集类型即通过
        var agentAssemblyName = typeof(EarCtrl.Agents.Huawei.Program).Assembly.GetName().Name;
        Assert.Equal("EarCtrl.Agent.Huawei", agentAssemblyName);
    }
}
