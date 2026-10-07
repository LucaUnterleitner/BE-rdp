using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RdpManager.App.Sessions;

namespace RdpManager.App.Views;

/// <summary>
/// Bar at the top of a full-screen session (its own top-most window, so it stays above the session).
/// It shrinks to a thin handle after a few seconds and opens again when the pointer touches it.
/// </summary>
public sealed class PinBarWindow : Window
{
    private readonly SessionTabManager _manager;
    private readonly TabHost _host;
    private readonly TextBlock _name = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
    private readonly Button _dock;
    private readonly StackPanel _content;
    private readonly DispatcherTimer _collapse;
    private bool _collapsed;

    public PinBarWindow(SessionTabManager manager, TabHost host)
    {
        _manager = manager;
        _host = host;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Owner = host.Window;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Title = "Session bar";
        _content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _content.Children.Add(_name);
        _content.Children.Add(Action("Leave full screen", "leave"));
        _dock = Action("Back to app", "dock");
        _content.Children.Add(_dock);
        _content.Children.Add(Action("Disconnect", "disconnect"));
        Content = _content;
        _collapse = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => { if (!IsMouseOver && !IsKeyboardFocusWithin) SetCollapsed(true); }, Dispatcher);
        MouseEnter += (_, _) => { _collapse.Stop(); SetCollapsed(false); };
        MouseLeave += (_, _) => _collapse.Start();
        Closed += (_, _) => _collapse.Stop();
        SetCollapsed(false);
        _collapse.Start();
    }

    private Button Action(string label, string action)
    {
        var b = new Button { Content = label, Style = (Style)Application.Current.Resources["Btn.Ghost.Small"], Foreground = Brushes.White, Margin = new Thickness(4, 0, 0, 0) };
        if (action == "leave") b.ToolTip = "Ctrl+Alt+Break";
        b.Click += (_, _) => _manager.PinbarAction(_host, action);
        return b;
    }

    public void Refresh()
    {
        _name.Text = _host.ActiveTab?.Name ?? "";
        _dock.Visibility = _host.IsMain ? Visibility.Collapsed : Visibility.Visible;
        Place();
    }

    private void SetCollapsed(bool on)
    {
        _collapsed = on;
        _content.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Place();
    }

    private void Place()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(_host.Window).Handle).Bounds;
        var dpi = VisualTreeHelper.GetDpi(_host.Window);
        var w = _collapsed ? 180 : 520;
        var h = _collapsed ? 8 : 44;
        Width = w;
        Height = h;
        Left = screen.Left / dpi.DpiScaleX + (screen.Width / dpi.DpiScaleX - w) / 2;
        Top = screen.Top / dpi.DpiScaleY;
    }
}
