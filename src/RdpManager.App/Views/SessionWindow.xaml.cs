using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using RdpManager.App.Sessions;
using RdpManager.App.ViewModels;

namespace RdpManager.App.Views;

/// <summary>A window made from session tabs dragged out of another window.</summary>
public partial class SessionWindow : Window
{
    private readonly SessionTabManager _manager;
    private SessionTab? _watched;

    public SessionWindow(SessionTabManager manager)
    {
        _manager = manager;
        InitializeComponent();
        Host = new TabHost(this, Slot, Strip, isMain: false);
        Strip.Bind(Host, manager, null);
        Host.PropertyChanged += (_, _) => Update();
        Host.Tabs.CollectionChanged += (_, _) => Update();
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    public TabHost Host { get; }

    private void Update()
    {
        var t = Host.ActiveTab;
        if (_watched != t)
        {
            if (_watched is not null) _watched.PropertyChanged -= OnTabChanged;
            _watched = t;
            if (t is not null) t.PropertyChanged += OnTabChanged;
        }
        Strip.Visibility = Host.Fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Title = $"{t?.Name ?? "Sessions"} – BearingPoint Remote Desktop";
        UpdatePlaceholder();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e) => UpdatePlaceholder();

    private void UpdatePlaceholder()
    {
        var t = Host.ActiveTab;
        Slot.Visibility = t is { Shown: true, IsFailed: false } ? Visibility.Visible : Visibility.Hidden;
        PlaceholderTitle.Text = t?.PlaceholderTitle ?? "";
        PlaceholderSpinner.Visibility = t is { IsFailed: false } ? Visibility.Visible : Visibility.Collapsed;
        if (Slot.Visibility == Visibility.Visible) _manager.Sync(Host);
    }

    /// <summary>Closing the window keeps its sessions: the tabs go back to the main window.</summary>
    private void OnClosing(object? sender, CancelEventArgs e) => _manager.OnHostClosing(Host);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    /// <summary>Dragging the window to the top edge (Windows Snap maximizes) means full screen for a session window.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Maximized || Host.Fullscreen || !GetCursorPos(out var p)) return;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
        if (p.Y <= screen.Bounds.Top + 4) _manager.SetFullscreen(Host, true);
    }
}
