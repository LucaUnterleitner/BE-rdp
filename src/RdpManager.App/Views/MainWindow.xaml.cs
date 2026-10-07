using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RdpManager.App.Sessions;
using RdpManager.App.ViewModels;
using RdpManager.Core.Json;
using RdpManager.Infrastructure.Storage;

namespace RdpManager.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppPaths _paths;
    private TabHost? _host;
    private SessionTabManager? _tabs;
    private bool _syncingNav;

    public MainWindow(MainViewModel vm, AppPaths paths)
    {
        _vm = vm;
        _paths = paths;
        InitializeComponent();
        DataContext = vm;
        RestorePlacement();
        vm.PropertyChanged += OnVmChanged;
        UpdateNav();
        UpdateSidebar();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Activated += (_, _) => (Application.Current as App)?.OnWindowForeground(true);
        StateChanged += (_, _) => (Application.Current as App)?.OnWindowForeground(WindowState != WindowState.Minimized);
        IsVisibleChanged += (_, e) => (Application.Current as App)?.OnWindowForeground((bool)e.NewValue);
    }

    public void AttachTabs(SessionTabManager tabs)
    {
        _tabs = tabs;
        _host = new TabHost(this, Slot, Strip, isMain: true);
        tabs.RegisterMain(_host);
        Strip.Bind(_host, tabs, _vm);
        _host.PropertyChanged += (_, _) => UpdateSessionArea();
        _host.Tabs.CollectionChanged += (_, _) => UpdateSessionArea();
        UpdateSessionArea();
    }

    private SessionTab? _watched;

    /// <summary>Shows the session area while a session tab is active, the app pages otherwise.</summary>
    private void UpdateSessionArea()
    {
        if (_host is null) return;
        var active = _host.ActiveTab;
        if (_watched != active)
        {
            if (_watched is not null) _watched.PropertyChanged -= OnActiveTabChanged;
            _watched = active;
            if (active is not null) active.PropertyChanged += OnActiveTabChanged;
        }
        SessionArea.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        Body.Visibility = active is null ? Visibility.Visible : Visibility.Collapsed;
        var fullscreen = _host.Fullscreen && active is not null;
        TopBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        if (fullscreen) Strip.Visibility = Visibility.Collapsed;
        else Strip.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(MainViewModel.ShowTabStrip)) { Converter = (System.Windows.Data.IValueConverter)Application.Current.Resources["Visible"] });
        UpdatePlaceholder();
        _vm.RaiseTabs();
    }

    private void OnActiveTabChanged(object? sender, PropertyChangedEventArgs e) => UpdatePlaceholder();

    private void UpdatePlaceholder()
    {
        var t = _host?.ActiveTab;
        Slot.Visibility = t is { Shown: true, IsFailed: false } ? Visibility.Visible : Visibility.Hidden;
        if (t is null) return;
        PlaceholderTitle.Text = t.PlaceholderTitle;
        PlaceholderMessage.Text = t.PlaceholderMessage;
        PlaceholderSpinner.Visibility = t.IsFailed ? Visibility.Collapsed : Visibility.Visible;
        if (Slot.Visibility == Visibility.Visible) _tabs?.Sync(_host!);
    }

    // ── Navigation ───────────────────────────────────────
    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (_syncingNav || sender is not RadioButton { Tag: string route }) return;
        _vm.Navigate(route);
    }

    private void UpdateNav()
    {
        _syncingNav = true;
        foreach (var rb in Nav.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == _vm.NavRoute;
        _syncingNav = false;
    }

    private void UpdateSidebar()
    {
        Sidebar.Width = _vm.SidebarCollapsed ? 72 : 240;
        foreach (var rb in Nav.Children.OfType<RadioButton>())
        {
            if (rb.Content is StackPanel sp && sp.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
                label.Visibility = _vm.SidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetIsEnabled(rb, _vm.SidebarCollapsed);
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.NavRoute): UpdateNav(); break;
            case nameof(MainViewModel.SidebarCollapsed): UpdateSidebar(); break;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var inText = Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox;
        if ((e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.None && !inText) || (e.Key == Key.Divide && !inText))
        {
            if (FindName("SearchBox") is TextBox || FocusSearch()) e.Handled = true;
        }
    }

    /// <summary>Focuses the search box of the current list page.</summary>
    private bool FocusSearch()
    {
        var box = FindChild<TextBox>(this, "SearchBox");
        if (box is null || !box.IsVisible) return false;
        box.Focus();
        box.SelectAll();
        return true;
    }

    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            if (FindChild<T>(child, name) is { } found) return found;
        }
        return null;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Application.Current is App app && !app.OnMainWindowClosing()) e.Cancel = true;
    }

    // ── Window placement (per computer) ──────────────────
    private void RestorePlacement()
    {
        var p = JsonFile.TryRead(_paths.WindowPlacementFile, RdpJsonContext.Default.WindowPlacementRecord);
        if (p is null || p.Width < MinWidth || p.Height < MinHeight) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(p.Left, p.Top, p.Width, 40);
        if (!screen.IntersectsWith(title)) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = Math.Min(p.Width, screen.Width);
        Height = Math.Min(p.Height, screen.Height);
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    public void SavePlacement()
    {
        try
        {
            if (_host?.Fullscreen == true) return;
            var r = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            JsonFile.Write(_paths.WindowPlacementFile, new WindowPlacementRecord { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = WindowState == WindowState.Maximized },
                RdpJsonContext.Default.WindowPlacementRecord);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
