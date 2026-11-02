using Microsoft.Win32;

namespace EarCtrl.App.Services;

/// <summary>
/// 开机自启（M4）：HKCU\...\Run 值写入/删除（HKCU 无需管理员权限，portable/安装版通用）。
/// 注册表即唯一状态来源，不额外持久化到 config。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EarCtrl Studio";

    /// <summary>开机自启启动参数：仅驻留托盘，不打开主界面。</summary>
    public const string SilentArgument = "--autostart";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    return false;
                }
                key.SetValue(ValueName, $"\"{exe}\" {SilentArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
