using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RdpManager.App.Sessions;
using RdpManager.App.ViewModels;

namespace RdpManager.App.Controls;

/// <summary>
/// Tab bar of a tab host: Home and page tabs (main window only) and session tabs. Dragging a session tab out of
/// the bar hands it to the tab manager, which then follows the cursor.
/// </summary>
public partial class TabStrip : UserControl
{
    private const double DragThreshold = 24;
    private Point? _down;
    private string? _downId;
    private bool _dragStarted;
    private TabHost? _host;
    private SessionTabManager? _manager;
    private MainViewModel? _shell;

    public TabStrip()
    {
        InitializeComponent();
    }

    /// <param name="shell">The main view model in the main window; null in detached session windows.</param>
    public void Bind(TabHost host, SessionTabManager manager, MainViewModel? shell)
    {
        _host = host;
        _manager = manager;
        _shell = shell;
        SessionTabs.ItemsSource = host.Tabs;
        host.PropertyChanged += OnHostChanged;
        host.Tabs.CollectionChanged += (_, _) => Update();
        if (shell is not null)
        {
            PageTabs.ItemsSource = shell.PageTabs;
            shell.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MainViewModel.HomeSelected)) Update(); };
        }
        else
        {
            HomeTab.Visibility = Visibility.Collapsed;
            PageTabs.Visibility = Visibility.Collapsed;
        }
        Update();
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        if (_host is null) return;
        var homeSelected = _shell?.HomeSelected == true;
        HomeTab.Style = (Style)Resources[homeSelected ? "ActiveTab" : "TabBorder"];
        HomeLine.Visibility = homeSelected ? Visibility.Visible : Visibility.Collapsed;
        Hint.Text = _host.Tabs.Count > 0 ? "Drag a session tab out to open it in its own window"
            : _host.Elsewhere ? "Drag a session tab here to bring it back into the app" : "";
        Bar.Background = (Brush)Application.Current.Resources[_host.DropHint ? "SelectedBgBrush" : "Gray100Brush"];
        Bar.BorderBrush = (Brush)Application.Current.Resources[_host.DropHint ? "PrimaryBrush" : "BorderBrush"];
        Bar.BorderThickness = new Thickness(0, 0, 0, _host.DropHint ? 3 : 1);
    }

    private void OnHome(object sender, RoutedEventArgs e) => _shell?.ShowHome();

    private static string? IdOf(object sender) => (sender as FrameworkElement)?.Tag as string;

    private void OnSessionClick(object sender, RoutedEventArgs e)
    {
        if (_dragStarted) { _dragStarted = false; return; }
        if (IdOf(sender) is { } id && _host is not null) _manager?.Activate(_host, id);
        _shell?.SyncPageTabs();
        _shell?.RaiseTabs();
    }

    private void OnSessionClose(object sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is { } id) MainViewModel.Instance.CloseSessionTabCommand.Execute(id);
    }

    private void OnSessionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && IdOf(sender) is { } id)
        {
            MainViewModel.Instance.CloseSessionTabCommand.Execute(id);
            e.Handled = true;
        }
    }

    private void OnSessionMenu(object sender, MouseButtonEventArgs e)
    {
        if (IdOf(sender) is { } id && _host is not null) _manager?.ShowMenu(_host, id, (UIElement)sender);
        e.Handled = true;
    }

    private void OnSessionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_host is null || _host.Fullscreen) return;
        _down = e.GetPosition(this);
        _downId = IdOf(sender);
        _dragStarted = false;
    }

    private void OnSessionMouseMove(object sender, MouseEventArgs e)
    {
        if (_down is null || _downId is null || e.LeftButton != MouseButtonState.Pressed || _dragStarted) return;
        var p = e.GetPosition(this);
        var outside = p.Y < -DragThreshold || p.Y > ActualHeight + DragThreshold;
        if (outside || Math.Abs(p.Y - _down.Value.Y) > DragThreshold * 2)
        {
            _dragStarted = true;
            var id = _downId;
            _down = null;
            Mouse.Capture(null);
            _manager?.DragStart(_host!, id);
        }
    }

    private void OnSessionMouseUp(object sender, MouseButtonEventArgs e)
    {
        _down = null;
        _downId = null;
    }
}
