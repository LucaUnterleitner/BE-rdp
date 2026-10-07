using System.Collections.ObjectModel;
using System.Windows;
using RdpManager.App.Controls;
using RdpManager.App.ViewModels;
using RdpManager.App.Views;
using RdpManager.Core.Models;

namespace RdpManager.App.Sessions;

/// <summary>A session tab in a tab strip.</summary>
public sealed class SessionTab(string id, string connectionId, string name, string host) : ObservableObject
{
    private SessionState _state = SessionState.Connecting;
    private bool _isActive;

    public string Id { get; } = id;
    public string ConnectionId { get; } = connectionId;
    public string Name { get; } = name;
    public string Host { get; } = host;

    public SessionState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(PlaceholderTitle));
            OnPropertyChanged(nameof(PlaceholderMessage));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private bool _shown;
    /// <summary>True once the remote desktop has something to show (the native slot stays hidden until then).</summary>
    public bool Shown { get => _shown; set => Set(ref _shown, value); }
    public bool IsBusy => _state is SessionState.Connecting or SessionState.Reconnecting;
    public bool IsFailed => _state is SessionState.Failed or SessionState.Ended;
    public string StateText => _state switch
    {
        SessionState.Connecting => "Connecting",
        SessionState.Active => "Connected",
        SessionState.Reconnecting => "Reconnecting",
        SessionState.Ended => "Ended",
        _ => "Failed",
    };
    public string AutomationName => $"{Name}, {StateText}";
    public string PlaceholderTitle => IsFailed ? $"Session to {Name} {(_state == SessionState.Failed ? "failed" : "ended")}" : $"Connecting to {Name}…";
    public string PlaceholderMessage => IsFailed ? "" : "Windows may ask for your password in a separate window.";
}

/// <summary>A window that holds session tabs: the main window or a window made from dragged-out tabs.</summary>
public sealed class TabHost : ObservableObject
{
    private string? _activeId;
    private bool _fullscreen;
    private bool _dropHint;
    private bool _elsewhere;

    public TabHost(Window window, SessionSlot slot, FrameworkElement strip, bool isMain)
    {
        Window = window;
        Slot = slot;
        Strip = strip;
        IsMain = isMain;
    }

    public Window Window { get; }
    public SessionSlot Slot { get; }
    public FrameworkElement Strip { get; }
    public bool IsMain { get; }
    public ObservableCollection<SessionTab> Tabs { get; } = [];
    public bool Closing { get; set; }
    internal PinBarWindow? Pinbar { get; set; }
    internal (WindowState State, WindowStyle Style, ResizeMode Resize, bool Topmost)? SavedWindow { get; set; }

    public string? ActiveId
    {
        get => _activeId;
        set
        {
            if (!Set(ref _activeId, value)) return;
            foreach (var t in Tabs) t.IsActive = t.Id == value;
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(HasActiveSession));
        }
    }

    public SessionTab? ActiveTab => Tabs.FirstOrDefault(t => t.Id == _activeId);
    public bool HasActiveSession => ActiveTab is not null;
    public bool Fullscreen { get => _fullscreen; set => Set(ref _fullscreen, value); }
    public bool DropHint { get => _dropHint; set => Set(ref _dropHint, value); }
    /// <summary>Main window only: session tabs live in other windows, so the tab bar stays visible as a drop target.</summary>
    public bool Elsewhere { get => _elsewhere; set => Set(ref _elsewhere, value); }

    public void NotifyTabsChanged()
    {
        foreach (var t in Tabs) t.IsActive = t.Id == _activeId;
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(HasActiveSession));
        OnPropertyChanged(nameof(Tabs));
    }
}
