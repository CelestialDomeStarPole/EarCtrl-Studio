using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using EarCtrl.Core.Config;

namespace EarCtrl.App.Services;

/// <summary>运行时 UI 全局状态（DevTools 入口可见性等）。</summary>
public sealed class UiState : INotifyPropertyChanged
{
    public static UiState Instance { get; } = new();

    private UiState()
    {
        _devToolsVisible = App.Config.DevToolsVisible;
    }

    private bool _devToolsVisible;

    public bool DevToolsVisible
    {
        get => _devToolsVisible;
        set
        {
            if (_devToolsVisible == value) return;
            _devToolsVisible = value;
            OnPropertyChanged(nameof(DevToolsVisibility));
            OnPropertyChanged(nameof(DevToolsVisible));
        }
    }

    public Visibility DevToolsVisibility => DevToolsVisible ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
