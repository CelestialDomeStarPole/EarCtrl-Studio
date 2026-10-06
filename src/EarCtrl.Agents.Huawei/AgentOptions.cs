// SPDX-License-Identifier: GPL-3.0-only

namespace EarCtrl.Agents.Huawei;

public sealed record AgentOptions
{
    public string? PipeName { get; init; }
    public string? Address { get; init; }
    public string? Command { get; init; }
    public string[] Arguments { get; init; } = [];
    public bool Verbose { get; init; }
    /// <summary>状态轮询间隔（毫秒；0=关闭）。主程序以 --poll 传入 AppConfig.PollIntervalSec。</summary>
    public int? PollMs { get; init; }

    public static AgentOptions Parse(string[] args)
    {
        var result = new AgentOptions();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    result = result with { PipeName = args[++i] };
                    break;
                case "--address" when i + 1 < args.Length:
                    result = result with { Address = args[++i] };
                    break;
                case "--poll" when i + 1 < args.Length:
                    result = result with { PollMs = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture) };
                    break;
                case "--verbose":
                    result = result with { Verbose = true };
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        return result with
        {
            Command = rest.FirstOrDefault(),
            Arguments = rest.Skip(1).ToArray(),
        };
    }
}
