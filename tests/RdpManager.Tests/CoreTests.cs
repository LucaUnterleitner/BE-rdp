using RdpManager.Core.Models;
using RdpManager.Core.Rdp;
using RdpManager.Core.Sessions;
using RdpManager.Core.Validation;

namespace RdpManager.Tests;

/// <summary>Ported from test/core.test.js of the Electron app, plus additional cases.</summary>
public class CoreTests
{
    private static readonly ConnectionDefaults D = new();
    internal static Connection Norm(Connection c) => ConnectionNormalizer.Normalize(c, D);
    private static Connection Base() => Norm(new Connection { Host = "srv01.corp.local", Username = "CORP\\jdoe" });

    [Fact]
    public void BuildRdp_writes_secure_defaults_and_no_password()
    {
        var p = RdpFile.ParseProperties(RdpFile.Build(Base()));
        Assert.Equal("srv01.corp.local", p["full address"]);
        Assert.Equal("CORP\\jdoe", p["username"]);
        Assert.Equal(1, p["enablecredsspsupport"]);
        Assert.Equal(2, p["authentication level"]);
        Assert.Equal("", p["drivestoredirect"]);
        Assert.Equal(2, p["screen mode id"]);
        Assert.False(p.ContainsKey("password 51"));
    }

    [Fact]
    public void BuildRdp_handles_port_window_mode_and_gateway()
    {
        var c = Base();
        c.Port = 3390;
        c.Display = new DisplayOptions { Mode = "window", Width = 1600, Height = 900 };
        c.Gateway = new GatewayOptions { Mode = "always", Host = "rdgw.corp.com" };
        c.Redirect!.Drives = "all";
        var p = RdpFile.ParseProperties(RdpFile.Build(c));
        Assert.Equal("srv01.corp.local:3390", p["full address"]);
        Assert.Equal(1, p["screen mode id"]);
        Assert.Equal(1600, p["desktopwidth"]);
        Assert.Equal("rdgw.corp.com", p["gatewayhostname"]);
        Assert.Equal(1, p["gatewayusagemethod"]);
        Assert.Equal("*", p["drivestoredirect"]);
    }

    [Fact]
    public void Encode_decode_round_trip_uses_utf16_le_with_bom()
    {
        var buf = RdpFile.Encode("full address:s:höst\r\n");
        Assert.Equal([0xFF, 0xFE], buf[..2]);
        Assert.Equal("full address:s:höst\r\n", RdpFile.Decode(buf));
        Assert.Equal("a:s:b", RdpFile.Decode([0xEF, 0xBB, 0xBF, (byte)'a', (byte)':', (byte)'s', (byte)':', (byte)'b']));
    }

    [Fact]
    public void Imported_rdp_files_are_treated_as_untrusted()
    {
        const string text = "full address:s:evil.example.com:4489\r\npassword 51:b:01000000D08C\r\nalternate shell:s:cmd.exe\r\ndrivestoredirect:s:*\r\nenablecredsspsupport:i:0\r\nauthentication level:i:0\r\nusername:s:bob\r\n";
        var (conn, warnings) = RdpFile.ToConnection(text, "evil.rdp");
        Assert.Equal("evil.example.com", conn.Host);
        Assert.Equal(4489, conn.Port);
        Assert.Equal("evil", conn.Name);
        Assert.Equal("none", conn.Redirect!.Drives);
        Assert.Equal(2, conn.Security!.AuthLevel);
        Assert.Equal(5, warnings.Count);
        var rebuilt = RdpFile.ParseProperties(RdpFile.Build(Norm(conn)));
        Assert.False(rebuilt.ContainsKey("alternate shell"));
        Assert.Equal(1, rebuilt["enablecredsspsupport"]);
    }

    [Fact]
    public void Direct_mstsc_arguments()
    {
        var c = Base();
        c.Display!.Multimon = true;
        c.Security!.AdminSession = true;
        c.Security.CredentialProtection = "remoteGuard";
        c.Gateway = new GatewayOptions { Mode = "detect", Host = "gw" };
        Assert.Equal(["/v:srv01.corp.local", "/f", "/multimon", "/admin", "/g:gw", "/remoteGuard"], RdpFile.BuildDirectArgs(c));
    }

    [Fact]
    public void Policy_locks_override_connection_settings_without_mutating_input()
    {
        var c = Base();
        c.Redirect!.Drives = "all";
        c.Redirect.Clipboard = true;
        var o = RdpFile.ApplyPolicy(c, new AppPolicy { Redirect = new PolicyRedirect { Drives = "none", Clipboard = false }, RequireCredentialProtection = "remoteGuard" });
        Assert.Equal("none", o.Redirect!.Drives);
        Assert.False(o.Redirect.Clipboard);
        Assert.Equal("remoteGuard", o.Security!.CredentialProtection);
        Assert.Equal("all", c.Redirect.Drives);
        Assert.Equal("none", c.Security!.CredentialProtection);
    }

    [Fact]
    public void Disconnect_reasons_map_to_friendly_text()
    {
        Assert.Equal("Computer name not found", DisconnectReasons.Explain(260)!.Title);
        Assert.Equal("0x204", DisconnectReasons.Explain(0x204)!.Code);
        Assert.Equal("normal", DisconnectReasons.Explain(1)!.Kind);
        Assert.Equal("0x9999", DisconnectReasons.Explain(0x9999)!.Code);
        Assert.Null(DisconnectReasons.Explain(null));
        Assert.Contains("VPN", DisconnectReasons.ExplainProbe("timeout").Message);
        Assert.Contains(DisconnectReasons.All(), x => x.Code == "0x807");
    }

    [Fact]
    public void Disconnect_reason_is_read_from_event_properties()
    {
        Assert.Equal(2308, MstscSessionStateMachine.ReasonOf(new RdpClientEvent(1026, 1, DateTimeOffset.UtcNow, ["Disconnect Reason", "2308", "Info"])));
        Assert.Null(MstscSessionStateMachine.ReasonOf(new RdpClientEvent(1026, 1, DateTimeOffset.UtcNow, ["x"])));
    }

    [Fact]
    public void Connection_validation_rejects_bad_input()
    {
        Assert.Contains("computer name", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "" })).Message);
        Assert.Contains("not allowed", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "a b" })).Message);
        Assert.Contains("port", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "x", Port = 70000 })).Message);
        Assert.Contains("Gateway", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "x", Gateway = new GatewayOptions { Mode = "always" } })).Message);
        Assert.Contains("Gateway", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "x", Gateway = new GatewayOptions { Mode = "always", Host = "gw host" } })).Message);
        Assert.Equal(["a", "b", "c"], ConnectionNormalizer.ParseTags("a, b ,,c"));
        Assert.Contains("Port field", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "srv:3390" })).Message);
        Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "x", Username = "a b" }));
    }

    [Fact]
    public void Control_characters_cannot_inject_rdp_properties()
    {
        var c = Base();
        c.Username = "bob\r\nalternate shell:s:cmd.exe";
        var text = RdpFile.Build(c);
        Assert.DoesNotMatch("(?m)^alternate shell", text);
        Assert.Equal("ab", Norm(new Connection { Host = "x", Username = "a\nb" }).Username);
    }

    [Fact]
    public void Sign_in_as_a_different_user()
    {
        var c = Base();
        c.PromptAlways = true;
        var p = RdpFile.ParseProperties(RdpFile.Build(c));
        Assert.Equal(1, p["prompt for credentials"]);
        Assert.False(p.ContainsKey("username"));
        Assert.Contains("/prompt", RdpFile.BuildDirectArgs(c));
    }

    [Fact]
    public void Ipv6_and_ports_are_parsed_correctly()
    {
        Assert.Equal(("fe80::1", 3389), RdpFile.ParseAddress("fe80::1"));
        Assert.Equal(("fe80::1", 3390), RdpFile.ParseAddress("[fe80::1]:3390"));
        Assert.Equal(("srv", 4000), RdpFile.ParseAddress("srv:4000"));
        var v6 = Norm(new Connection { Host = "fe80::1", Port = 3390 });
        Assert.Equal("[fe80::1]:3390", RdpFile.ParseProperties(RdpFile.Build(v6))["full address"]);
    }

    [Fact]
    public void Connection_ids_must_be_plain_uuids()
    {
        var evil = Norm(new Connection { Id = "\"><img src=x onerror=alert(1)>", Host = "x" });
        Assert.Matches("^[0-9a-f-]{36}$", evil.Id);
        var ok = Guid.NewGuid().ToString();
        Assert.Equal(ok, Norm(new Connection { Id = ok, Host = "x" }).Id);
        Assert.Null(Norm(new Connection { Host = "x", LastConnectedAt = "5 apples" }).LastConnectedAt);
        Assert.True(ConnectionNormalizer.IsCentralId("central-" + ok));
        Assert.False(ConnectionNormalizer.IsCentralId("central-../../x"));
    }

    [Fact]
    public void Normalization_limits_lengths_and_keeps_options()
    {
        var c = Norm(new Connection { Host = "x", Name = new string('n', 200), Tags = Enumerable.Range(0, 20).Select(i => $"t{i}").ToList(), Security = new SecurityOptions { AuthLevel = 1, CredentialProtection = "bogus" } });
        Assert.Equal(120, c.Name.Length);
        Assert.Equal(12, c.Tags.Count);
        Assert.Equal(1, c.Security!.AuthLevel);
        Assert.Equal("none", c.Security.CredentialProtection);
        Assert.Equal("x", Norm(new Connection { Host = "x" }).Name);
    }

    [Fact]
    public void Settings_are_normalized()
    {
        var s = SettingsNormalizer.Normalize(new AppSettings { LaunchMode = "evil", StatusRefreshSeconds = 5, SigningThumbprint = "ab:cd ef", SessionWindow = "?" });
        Assert.Equal("file", s.LaunchMode);
        Assert.Equal(30, s.StatusRefreshSeconds);
        Assert.Equal("ABCDEF", s.SigningThumbprint);
        Assert.Equal("tabs", s.SessionWindow);
        Assert.Equal(600, SettingsNormalizer.Normalize(new AppSettings { StatusRefreshSeconds = 99999 }).StatusRefreshSeconds);
        // Same as normalizeDefaults in store.js: a gateway without an address is rejected.
        Assert.Throws<ValidationException>(() => ConnectionNormalizer.NormalizeDefaults(new ConnectionDefaults { Gateway = new GatewayOptions { Mode = "always", Host = "" } }));
        Assert.Equal("gw.corp", ConnectionNormalizer.NormalizeDefaults(new ConnectionDefaults { Gateway = new GatewayOptions { Mode = "always", Host = "gw.corp" } }).Gateway.Host);
    }
}
