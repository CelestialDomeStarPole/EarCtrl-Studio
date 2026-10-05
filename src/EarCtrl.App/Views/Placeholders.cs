namespace EarCtrl.App.Views;

/// <summary>占位页具体类型：WPF-UI NavigationView 的 TargetPageType 要求唯一类型。</summary>
public sealed class EqualizerPlaceholder : UnderConstructionPage
{
    public EqualizerPlaceholder() : base("Nav.Equalizer") { }
}

public sealed class ControlsPlaceholder : UnderConstructionPage
{
    public ControlsPlaceholder() : base("Nav.Controls") { }
}

public sealed class IntegrationPlaceholder : UnderConstructionPage
{
    public IntegrationPlaceholder() : base("Nav.Integration") { }
}

public sealed class AdvancedPlaceholder : UnderConstructionPage
{
    public AdvancedPlaceholder() : base("Nav.Advanced") { }
}

public sealed class DevToolsPlaceholder : UnderConstructionPage
{
    public DevToolsPlaceholder() : base("Nav.DevTools") { }
}
