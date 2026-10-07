using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RdpManager.App.ViewModels;
using RdpManager.Core.Models;

namespace RdpManager.App.Views.Dialogs;

public partial class EditSystemView : UserControl
{
    private readonly EditSystemViewModel _vm;

    public EditSystemView(EditSystemViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        TypeRdp.Visibility = vm.Types.Contains(Protocols.Rdp) ? Visibility.Visible : Visibility.Collapsed;
        TypeSsh.Visibility = vm.Types.Contains(Protocols.Ssh) ? Visibility.Visible : Visibility.Collapsed;
        TypeWeb.Visibility = vm.Types.Contains(Protocols.Web) ? Visibility.Visible : Visibility.Collapsed;
        // A pasted full address ("ssh admin@host", "https://ilo01") fills the fields.
        HostInput.LostFocus += (_, _) => vm.DetectFromHost();
        DataObject.AddPastingHandler(HostInput, (_, _) => Dispatcher.BeginInvoke(vm.DetectFromHost));
        vm.PropertyChanged += OnChanged;
        ApplyType();
    }

    public TextBox HostBox => HostInput;

    public string Password => PasswordInput.Password;

    public void ClearPassword() => PasswordInput.Clear();

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditSystemViewModel.Protocol)) ApplyType();
    }

    /// <summary>Shows only what the connection type needs. In tabs mode the Display tab moves into Advanced settings.</summary>
    private void ApplyType()
    {
        var rdp = _vm.IsRdp;
        var tabsMode = _vm.Options.TabsMode;
        DisplayTab.Visibility = rdp && !tabsMode ? Visibility.Visible : Visibility.Collapsed;
        DevicesTab.Visibility = rdp ? Visibility.Visible : Visibility.Collapsed;
        AdvancedTab.Visibility = rdp ? Visibility.Visible : Visibility.Collapsed;
        InlineDisplay.Visibility = rdp && tabsMode ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = rdp && _vm.AllowSavedPassword ? Visibility.Visible : Visibility.Collapsed;
        if (!rdp) Tabs.SelectedItem = GeneralTab;
    }

    public void FocusProblem(bool general)
    {
        if (general)
        {
            Tabs.SelectedItem = GeneralTab;
            Dispatcher.BeginInvoke(() => HostInput.Focus());
        }
        else
        {
            Tabs.SelectedItem = AdvancedTab;
            Dispatcher.BeginInvoke(Advanced.FocusGateway);
        }
    }

    private void OnPortInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
}
