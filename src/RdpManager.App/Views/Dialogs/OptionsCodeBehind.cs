using System.Windows.Controls;

namespace RdpManager.App.Views.Dialogs;

public partial class DisplayOptions : UserControl
{
    public DisplayOptions() => InitializeComponent();
}

public partial class DeviceOptions : UserControl
{
    public DeviceOptions() => InitializeComponent();
}

public partial class AdvancedOptions : UserControl
{
    public AdvancedOptions() => InitializeComponent();

    public void FocusGateway() => GatewayHost.Focus();
}
