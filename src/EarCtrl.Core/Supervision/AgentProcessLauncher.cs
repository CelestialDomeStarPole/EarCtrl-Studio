using System.Diagnostics;
using EarCtrl.Core.Config;
using EarCtrl.Core.Profiles;

namespace EarCtrl.Core.Supervision;

/// <summary>进程拉起抽象（单测注入 fake，不真起进程）。</summary>
public interface IAgentProcessLauncher
{
    /// <summary>拉起 Agent exe。返回进程对象（用于 Kill）。</summary>
    Task<Process> StartAsync(DeviceProfile profile, AppConfig config);

    /// <summary>进程退出事件（brand, exitCode——null 表示被 Kill 或未知）。</summary>
    event Action<string, int?>? ProcessExited;
}

public sealed class AgentProcessLauncher : IAgentProcessLauncher
{
    public event Action<string, int?>? ProcessExited;

    public Task<Process> StartAsync(DeviceProfile profile, AppConfig config)
    {
        var exeDir = AppContext.BaseDirectory;
        var exePath = Path.Combine(exeDir, profile.AgentExe);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Agent exe 不存在: {exePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--pipe {profile.PipeName} --profile \"{profile.SourcePath ?? Path.Combine(exeDir, "profiles", $"{profile.Brand}.device.json")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = exeDir,
        };
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Agent 进程启动失败: {exePath}");

        var brand = profile.Brand;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => ProcessExited?.Invoke(brand, process.HasExited ? process.ExitCode : null);

        return Task.FromResult(process);
    }
}
