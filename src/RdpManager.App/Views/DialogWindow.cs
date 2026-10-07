using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RdpManager.App.Controls;

namespace RdpManager.App.Views;

/// <summary>
/// Modal dialog: a native window (title bar, Alt+F4, Esc) with optional subtitle, scrollable body and a footer
/// with buttons. Dialogs are separate top-level windows, so a session window in a tab never covers them.
/// </summary>
public sealed class DialogWindow : Window
{
    private readonly StackPanel _footer;
    private readonly ContentControl _body;
    private readonly ContentControl _note;

    public DialogWindow(string title, string? subtitle, object body, double width = 560)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["SurfaceBrush"];
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 14;
        Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Owner = ActiveOwner();

        var root = new DockPanel { LastChildFill = true };
        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["Text.Section"], TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(subtitle))
            header.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["Text.Secondary"], Margin = new Thickness(0, 2, 0, 0), FontSize = 14 });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 16, 24, 16) };
        var footerBorder = new Border { BorderBrush = (Brush)Application.Current.Resources["BorderBrush"], BorderThickness = new Thickness(0, 1, 0, 0), Child = _footer };
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);

        var content = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };
        _note = new ContentControl { Focusable = false };
        _body = new ContentControl { Content = body, Focusable = false };
        content.Children.Add(_note);
        content.Children.Add(_body);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Dismissible) { e.Handled = true; Close(); } };
        Loaded += (_, _) => FocusFirst();
    }

    /// <summary>False for dialogs that must be answered (no Esc, close button still cancels).</summary>
    public bool Dismissible { get; set; } = true;

    public object? BodyContent { get => _body.Content; set => _body.Content = value; }

    /// <summary>Element that gets the keyboard focus when the dialog opens (default: the first focusable control).</summary>
    public IInputElement? InitialFocus { get; set; }

    public static Window? ActiveOwner()
    {
        var app = Application.Current;
        return app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? (app?.MainWindow is { IsVisible: true } m ? m : null);
    }

    public Button AddButton(string label, string style, Action onClick, bool isDefault = false, bool isCancel = false, string? icon = null)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources[style],
            IsDefault = isDefault,
            IsCancel = isCancel,
            Margin = new Thickness(8, 0, 0, 0),
            Content = ButtonContent(label, icon),
        };
        AutomationLabel(button, label);
        button.Click += (_, _) => onClick();
        _footer.Children.Add(button);
        return button;
    }

    public void ClearButtons() => _footer.Children.Clear();

    public static object ButtonContent(string label, string? icon, bool iconAfter = false)
    {
        if (icon is null) return label;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var i = new Icon { Data = (Geometry)Application.Current.Resources["Icon." + icon], Size = 16, Margin = iconAfter ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 8, 0) };
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (iconAfter) { panel.Children.Add(t); panel.Children.Add(i); }
        else { panel.Children.Add(i); panel.Children.Add(t); }
        return panel;
    }

    private static void AutomationLabel(UIElement e, string label) => System.Windows.Automation.AutomationProperties.SetName(e, label);

    /// <summary>Shows an alert above the body (form errors).</summary>
    public void ShowNote(string kind, string title, string message)
    {
        _note.Content = Alert(kind, title, message);
    }

    public static Border Alert(string kind, string title, string message, string? technical = null)
    {
        var res = Application.Current.Resources;
        var (bg, border, fg, icon) = kind switch
        {
            "error" => ("ErrorBgBrush", "ErrorBorderBrush", "ErrorBrush", "xCircle"),
            "warning" => ("WarningBgBrush", "WarningBorderBrush", "WarningBrush", "warning"),
            "success" => ("SuccessBgBrush", "SuccessBorderBrush", "SuccessBrush", "checkCircle"),
            _ => ("InfoBgBrush", "InfoBorderBrush", "InfoBrush", "info"),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Icon { Data = (Geometry)res["Icon." + icon], Foreground = (Brush)res[fg], VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (message.Length > 0) text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["Gray700Brush"], Margin = new Thickness(0, 2, 0, 0) });
        if (technical is not null)
        {
            var exp = new Expander { Header = "Technical details", Margin = new Thickness(0, 8, 0, 0) };
            exp.Content = new TextBox { Text = technical, IsReadOnly = true, Style = (Style)res["Input.Mono"], TextWrapping = TextWrapping.Wrap, MinHeight = 0, Padding = new Thickness(6), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            text.Children.Add(exp);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border
        {
            Background = (Brush)res[bg], BorderBrush = (Brush)res[border], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16), Child = grid,
        };
    }

    private void FocusFirst()
    {
        if (InitialFocus is not null) { Keyboard.Focus(InitialFocus); return; }
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // ── Standard dialogs ────────────────────────────────
    public static bool Confirm(string title, string message, string confirmLabel, bool danger = false, string? detail = null, string cancelLabel = "Cancel")
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (detail is not null) body.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.Resources["Text.Secondary"], Margin = new Thickness(0, 12, 0, 0) });
        var dlg = new DialogWindow(title, null, body, 460);
        var answer = false;
        var cancel = dlg.AddButton(cancelLabel, "Btn.Secondary", dlg.Close, isCancel: true);
        var ok = dlg.AddButton(confirmLabel, danger ? "Btn.DangerSolid" : "Btn.Primary", () => { answer = true; dlg.Close(); }, isDefault: !danger);
        // Destructive confirmations start on Cancel so a stray Enter does not delete anything.
        dlg.InitialFocus = danger ? cancel : ok;
        dlg.ShowDialog();
        return answer;
    }

    /// <summary>Errors stay until the user closes them (never in a disappearing toast).</summary>
    public static void Error(string title, string message, string? technical = null)
    {
        // The title is in the header; the alert carries the explanation only.
        var dlg = new DialogWindow(title, null, Alert("error", message, "", technical), 480);
        var close = dlg.AddButton("Close", "Btn.Secondary", dlg.Close, isDefault: true, isCancel: true);
        dlg.InitialFocus = close;
        dlg.ShowDialog();
    }
}
