using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace EarCtrl.App.Services;

/// <summary>
/// 本地化服务（K5 中英双语框架）：默认 zh-CN（中立资源 Strings.resx），
/// 切换语言 = 设置 CurrentUICulture + 通知所有绑定刷新。
/// </summary>
public static class Loc
{
    private static readonly ResourceManager ResourceManager =
        new("EarCtrl.App.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>语言切换事件，LocSource 订阅后刷新全部绑定。</summary>
    public static event EventHandler? CultureChanged;

    public static string Tr(string key) =>
        ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static void SetCulture(string name)
    {
        CultureInfo.CurrentUICulture = new CultureInfo(name);
        CultureChanged?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>XAML 绑定入口：Text="{Binding Tr[Nav.Dashboard], Source={x:Static loc:LocSource.Instance}}"。</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource() => Loc.CultureChanged += (_, _) => OnPropertyChanged("Item[]");

    public string this[string key] => Loc.Tr(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>语言选项（设置页 ComboBox 数据源）。</summary>
public sealed record LanguageOption(string Code, string DisplayName)
{
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new("zh-CN", "中文"),
        new("en-US", "English"),
    ];
}
