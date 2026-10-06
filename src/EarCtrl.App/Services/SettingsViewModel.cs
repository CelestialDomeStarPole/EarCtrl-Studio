using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using EarCtrl.App.Services;
using EarCtrl.Core.Config;

namespace EarCtrl.App.Services;

/// <summary>设置页 VM：主题/语言切换立即生效并持久化（M0 验收项）。</summary>
public partial class SettingsViewModel : ObservableObject
{
    public static SettingsViewModel Instance { get; } = new();

    public IReadOnlyList<KeyValuePair<string, string>> ThemeOptions { get; } =
    [
        new(ThemeService.FollowSystem, Loc.Tr("Settings.Theme.FollowSystem")),
        new(ThemeService.Dark, Loc.Tr("Settings.Theme.Dark")),
        new(ThemeService.Light, Loc.Tr("Settings.Theme.Light")),
    ];

    public IReadOnlyList<LanguageOption> LanguageOptions => LanguageOption.All;

    /// <summary>状态轮询间隔选项（秒；顺序 1s→5s→关闭；0=关闭）。电量靠耳机事件推送，轮询仅驱动在仓/佩戴检测。</summary>
    public IReadOnlyList<KeyValuePair<int, string>> PollOptions { get; } =
    [
        new(1, "1s"),
        new(2, "2s"),
        new(3, "3s"),
        new(4, "4s"),
        new(5, "5s"),
        new(0, Loc.Tr("Settings.Poll.Off")),
    ];

    [ObservableProperty]
    private string _selectedTheme = App.Config.Theme;

    [ObservableProperty]
    private string _selectedLanguage = App.Config.Language;

    [ObservableProperty]
    private int _selectedPollIntervalSec = App.Config.PollIntervalSec;

    partial void OnSelectedThemeChanged(string value)
    {
        App.Config.Theme = value;
        ConfigStore.Save(App.Config);
        ThemeService.Apply(value);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        App.Config.Language = value;
        ConfigStore.Save(App.Config);
        Loc.SetCulture(value);
    }

    partial void OnSelectedPollIntervalSecChanged(int value)
    {
        App.Config.PollIntervalSec = value;
        ConfigStore.Save(App.Config);
        _ = AgentHost.Instance.ApplyPollIntervalAsync(value); // 对在跑 Agent 热更新（失败则下次拉起生效）
    }
}
