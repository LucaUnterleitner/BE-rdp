using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RdpManager.App.ViewModels;

namespace RdpManager.App.Views.Dialogs;

public partial class ConnectOptionsView : UserControl
{
    public ConnectOptionsView(ConnectOptionsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    public void ShowAdvanced()
    {
        AdvancedExpander.IsExpanded = true;
        Dispatcher.BeginInvoke(Advanced.FocusGateway);
    }
}

public partial class QuickConnectView : UserControl
{
    private readonly QuickConnectViewModel _vm;

    public QuickConnectView(QuickConnectViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.CaretIndex = InputBox.Text.Length; };
    }

    public event Action<QuickOption>? Chosen;
    public TextBox Input => InputBox;

    private void OnKey(object sender, KeyEventArgs e)
    {
        var n = _vm.Options.Count;
        switch (e.Key)
        {
            case Key.Down when n > 0: _vm.Index = (_vm.Index + 1) % n; Options.ScrollIntoView(_vm.Selected); e.Handled = true; break;
            case Key.Up when n > 0: _vm.Index = (_vm.Index - 1 + n) % n; Options.ScrollIntoView(_vm.Selected); e.Handled = true; break;
            case Key.Enter:
                e.Handled = true;
                if (_vm.Selected is { } o) Chosen?.Invoke(o);
                break;
        }
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Options.SelectedItem is QuickOption o) Chosen?.Invoke(o);
    }
}

public partial class ConnectProgressView : UserControl
{
    public ConnectProgressView(ConnectProgressViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}

public partial class ImportPreviewView : UserControl
{
    public ImportPreviewView(IReadOnlyList<ImportPreviewRow> rows, string? more)
    {
        InitializeComponent();
        Items.ItemsSource = rows;
        More.Text = more ?? "";
        More.Visibility = more is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

public partial class NotificationsView : UserControl
{
    public NotificationsView(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}

/// <summary>Username and password fields. The password stays in the PasswordBox and is cleared after use.</summary>
public sealed class CredentialForm : StackPanel
{
    private readonly TextBox _user;
    private readonly PasswordBox _password;
    private readonly TextBlock _error;

    public CredentialForm(string username, bool showInfo, string host, string? intro = null)
    {
        var res = Application.Current.Resources;
        if (intro is not null) Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        if (showInfo)
        {
            Children.Add(DialogWindow.Alert("info", "Where is the password stored?",
                $"In Windows Credential Manager on this computer, protected by your Windows account (entry TERMSRV/{host}). This app never stores or shows it. Single sign-on or typing the password each time is safer on shared computers."));
        }
        Children.Add(new TextBlock { Text = "Username", Style = (Style)res["Text.Label"] });
        _user = new TextBox { Text = username };
        System.Windows.Automation.AutomationProperties.SetName(_user, "Username");
        Children.Add(_user);
        Children.Add(new TextBlock { Text = "Password", Style = (Style)res["Text.Label"], Margin = new Thickness(0, 16, 0, 4) });
        _password = new PasswordBox();
        System.Windows.Automation.AutomationProperties.SetName(_password, "Password");
        _password.PasswordChanged += (_, _) => PasswordChanged?.Invoke();
        Children.Add(_password);
        _error = new TextBlock { Foreground = (Brush)res["ErrorBrush"], FontSize = 13, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Children.Add(_error);
    }

    public event Action? PasswordChanged;
    public string Username => _user.Text.Trim();
    public string Password => _password.Password;
    public IInputElement InitialFocus => _user.Text.Length > 0 ? _password : _user;

    public string Error
    {
        set
        {
            _error.Text = value;
            _error.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public void Clear() => _password.Clear();
}
