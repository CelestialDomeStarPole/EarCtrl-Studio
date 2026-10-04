using System.Windows;
using System.Windows.Threading;
using EarCtrl.App.Services;
using EarCtrl.Core.Config;

namespace EarCtrl.App;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Config = ConfigStore.Load();
        Loc.SetCulture(Config.Language);

        var window = new MainWindow();
        ThemeService.Apply(Config.Theme);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ConfigStore.Save(Config);
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // M0：兜底避免闪退；结构化日志在 M2 引入
        MessageBox.Show(e.Exception.ToString(), "EarCtrl Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
