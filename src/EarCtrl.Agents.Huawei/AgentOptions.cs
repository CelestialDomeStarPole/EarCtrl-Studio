// SPDX-License-Identifier: GPL-3.0-only

namespace EarCtrl.Agents.Huawei;

public sealed record AgentOptions
{
    public string? PipeName { get; init; }
    public string? Address { get; init; }
    public string? Command { get; init; }
    public string[] Arguments { get; init; } = [];
    public bool Verbose { get; init; }

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
