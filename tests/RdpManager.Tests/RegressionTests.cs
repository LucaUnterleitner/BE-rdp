using System.Text.Json;
using RdpManager.Core.Import;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Rdp;

namespace RdpManager.Tests;

/// <summary>Regression tests for defects found in the code review.</summary>
public class RegressionTests
{
    [Fact]
    public void Partial_option_objects_are_merged_with_the_defaults_field_by_field()
    {
        var defaults = new ConnectionDefaults
        {
            Redirect = new RedirectOptions { Clipboard = false, Printers = false },
            Gateway = new GatewayOptions { Mode = "none", Host = "rdgw.corp" },
        };
        using var doc = JsonDocument.Parse("""{"host":"x","redirect":{"printers":true},"gateway":{"mode":"always"}}""");
        var c = ConnectionJson.FromJson(doc.RootElement, defaults)!;
        Assert.True(c.Redirect!.Printers);
        Assert.False(c.Redirect.Clipboard); // stays least-privilege, as in the Electron app
        Assert.Equal(("always", "rdgw.corp"), (c.Gateway!.Mode, c.Gateway.Host));
        Assert.Equal(1920, c.Display!.Width);
    }

    [Fact]
    public void Oversized_numbers_in_imports_do_not_abort_the_import()
    {
        Assert.Equal(("::1", 3389), RdpFile.ParseAddress("[::1]:99999999999"));
        var rdg = """<RDCMan><file><server><properties><name>h</name></properties><remoteDesktop><size>99999999999 x 5</size></remoteDesktop></server></file></RDCMan>""";
        var r = ConnectionImporters.ParseRdg(rdg);
        Assert.Equal(1920, r.Items[0].Connection.Display!.Width);
    }
}
