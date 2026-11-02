using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace EarCtrl.NotifyVerify;

/// <summary>
/// 托盘通知验证工具：用与 App 相同的 WinForms NotifyIcon 弹一次低电量气泡，观察系统通知样式。
/// 独立进程验证，不触发 App 的真实低电量判定；通知来源名会显示为本工具名（系统按 exe 归属）。
/// 用法：直接运行（双击或 dotnet run），气泡显示 3 秒，进程约 5 秒后自动退出。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "earctrl.ico");
        using var icon = new NotifyIcon
        {
            Text = "EarCtrl Studio",
            Visible = true,
        };
        if (File.Exists(iconPath))
        {
            icon.Icon = new Icon(iconPath);
        }

        // 文案与 TrayService.CheckLowBattery 一致（默认阈值 20%）
        icon.ShowBalloonTip(3000, "EarCtrl Studio",
            "HUAWEI FreeBuds SE 4 ANC 电量低 20%", ToolTipIcon.Warning);

        // 保持消息泵运行让气泡停留，随后退出
        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            icon.Visible = false;
            Application.Exit();
        };
        timer.Start();
        Application.Run();
    }
}
