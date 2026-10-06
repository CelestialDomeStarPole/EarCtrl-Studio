// SPDX-License-Identifier: GPL-3.0-only
// 华为 Agent 入口：仅服务模式——由 EarCtrl.App 以 --pipe 参数拉起，进入 IPC 服务。
// 真机诊断 CLI（monitor/polltest/selftest 等）已迁至 tools/EarCtrl.Huawei.Diagnostics（不进发行版）。

using EarCtrl.Agents.Huawei;
using EarCtrl.Ipc.NamedPipe;

var options = AgentOptions.Parse(args);

if (options.PipeName is null)
{
    Console.Out.WriteLine("""
        用法（本程序由 EarCtrl.App 自动拉起，无需手动运行）:
          EarCtrl.Agent.Huawei --pipe <pipeName> [--profile <path>] [--log <level>]
        真机诊断工具请使用 tools/EarCtrl.Huawei.Diagnostics 项目。
        """);
    return 2;
}

return await RunServiceModeAsync(options);

/// <summary>M2 服务模式：单实例幂等 → 起 NamedPipe 服务 → AgentRpc 接管；孤儿/关闭信号驱动退出。</summary>
static async Task<int> RunServiceModeAsync(AgentOptions options)
{
    // 单实例幂等（§3.1-B）：管道已存在说明 Agent 在跑，直接退出（复用语义）。
    // 探测两次（消拉起时序竞态）；探测用短超时快速失败——管道不存在时 ConnectAsync 会自旋等满超时，
    // 长超时会把冷启动拖慢数秒，导致主程序 3s 内连不上（首次连接必失败的根因）。
    for (var attempt = 0; attempt < 2; attempt++)
    {
        try
        {
            var probe = new NamedPipeClient(options.PipeName!);
            await using (probe.ConfigureAwait(false))
            {
                await probe.ConnectAsync(300);
            }
            Console.Out.WriteLine($"[service] {options.PipeName} 已有实例在运行，本进程退出（复用现有实例）");
            return 0;
        }
        catch
        {
            if (attempt == 0)
            {
                await Task.Delay(500);
            }
        }
    }

    var link = new HuaweiSppClient();
    var server = new NamedPipeServer(options.PipeName!);
    // TryStart 同步创建管道实例：被占（竞态期另一实例抢先）则本进程退出，绝不产生双实例
    if (!server.TryStart())
    {
        Console.Out.WriteLine($"[service] {options.PipeName} 已有实例在运行，本进程退出（复用现有实例）");
        return 0;
    }
    var rpc = new AgentRpc(link, server, new AgentRpcOptions());
    rpc.Start();
    Console.Out.WriteLine($"[service] IPC 服务端就绪: {options.PipeName}");

    await rpc.ShutdownSignal;
    Console.Out.WriteLine("[service] graceful exit (0)");
    await rpc.DisposeAsync();
    return 0;
}
