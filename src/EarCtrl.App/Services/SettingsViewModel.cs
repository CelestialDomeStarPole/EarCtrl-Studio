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

    [ObservableProperty]
    private string _selectedTheme = App.Config.Theme;

    [ObservableProperty]
    private string _selectedLanguage = App.Config.Language;

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
}
