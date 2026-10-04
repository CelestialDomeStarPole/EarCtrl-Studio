// EarCtrl.Agent.Huawei — 华为耳机协议 Agent（独立后台进程）
// SPDX-License-Identifier: GPL-3.0-only
//
// 本项目（EarCtrl.Agent.Huawei）的华为协议层将转写自 OpenFreebuds（GPL-3.0）。
// 许可证全文见本目录 LICENSE 文件。
//
// 运行参数（由主程序 EarCtrl.App 拉起时传入，见《软件实现计划》§3.1-B）：
//   --pipe <pipeName>     IPC 管道名（earctrl.agent.huawei）
//   --profile <path>      设备描述 JSON 路径
//   --log <level>         日志级别 trace|debug|info|warn|error

namespace EarCtrl.Agents.Huawei;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = AgentOptions.Parse(args);
        Console.Out.WriteLine($"[EarCtrl.Agent.Huawei] starting, pipe={options.PipeName}, profile={options.ProfilePath}, log={options.LogLevel}");

        // M0 骨架：仅校验参数并保持进程存活；IPC 服务端在 M2 接入。
        var exitTcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exitTcs.TrySetResult(); };

        await exitTcs.Task.ConfigureAwait(false);
        Console.Out.WriteLine("[EarCtrl.Agent.Huawei] graceful exit (0)");
        return 0;
    }
}

/// <summary>Agent 启动参数。真实 IPC 服务端在 M2 实现（EarCtrl.Ipc）。</summary>
internal sealed record AgentOptions
{
    public string PipeName { get; init; } = "earctrl.agent.huawei";
    public string ProfilePath { get; init; } = "";
    public string LogLevel { get; init; } = "info";

    public static AgentOptions Parse(string[] args)
    {
        var result = new AgentOptions();
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--pipe": result = result with { PipeName = args[i + 1] }; break;
                case "--profile": result = result with { ProfilePath = args[i + 1] }; break;
                case "--log": result = result with { LogLevel = args[i + 1] }; break;
            }
        }
        return result;
    }
}
