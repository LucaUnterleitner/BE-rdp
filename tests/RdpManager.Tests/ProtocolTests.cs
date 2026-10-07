using RdpManager.Core.Models;
using RdpManager.Core.Targets;
using RdpManager.Core.Validation;
using RdpManager.Infrastructure.Launchers;
using static RdpManager.Tests.CoreTests;

namespace RdpManager.Tests;

/// <summary>Ported from test/protocols.test.js (SSH and web are implemented but switched off in the UI).</summary>
public class ProtocolTests
{
    [Fact]
    public void Existing_records_without_a_type_stay_rdp()
    {
        var c = Norm(new Connection { Host = "srv01", Protocol = "" });
        Assert.Equal("rdp", c.Protocol);
        Assert.Equal(3389, c.Port);
    }

    [Fact]
    public void Default_ports_follow_the_type()
    {
        Assert.Equal(22, Norm(new Connection { Host = "h", Protocol = "ssh" }).Port);
        Assert.Equal(443, Norm(new Connection { Host = "h", Protocol = "web" }).Port);
        Assert.Equal(80, Norm(new Connection { Host = "h", Protocol = "web", Web = new WebOptions { Scheme = "http" } }).Port);
    }

    [Fact]
    public void Unknown_types_are_rejected() => Assert.Contains("not supported", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "vnc" })).Message);

    [Theory]
    [InlineData("-oProxyCommand=calc")]
    [InlineData(".hidden")]
    [InlineData("a;calc")]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("$(x)")]
    [InlineData("`x`")]
    [InlineData("a b")]
    [InlineData("a\nb")]
    [InlineData("a\"b")]
    public void Hosts_that_look_like_options_or_contain_shell_characters_are_rejected(string host)
        => Assert.Throws<ValidationException>(() => Norm(new Connection { Host = host, Protocol = "ssh" }));

    [Fact]
    public void Ssh_user_names_are_strict()
    {
        Assert.Equal("CORP\\alice", Norm(new Connection { Host = "h", Protocol = "ssh", Username = "CORP\\alice" }).Username);
        Assert.Equal("alice@corp.example", Norm(new Connection { Host = "h", Protocol = "ssh", Username = "alice@corp.example" }).Username);
        foreach (var u in new[] { "-oProxyCommand=x", "a;b", "a b", "a$(x)", "a%PATH%", "a&b", "trail\\" })
            Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "ssh", Username = u }));
    }

    [Fact]
    public void Ssh_key_file_and_jump_host_are_validated()
    {
        Assert.Contains("full path", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "ssh", Ssh = new SshOptions { IdentityFile = "id_rsa" } })).Message);
        Assert.Contains("full path", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "ssh", Ssh = new SshOptions { IdentityFile = "C:\\keys\\a&calc" } })).Message);
        Assert.Contains("jump host", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "ssh", Ssh = new SshOptions { JumpHost = "a;b" } })).Message);
        Assert.Contains("jump host", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "ssh", Ssh = new SshOptions { JumpHost = "-oProxyCommand=x" } })).Message);
        Assert.Equal("bob@bastion.corp:2222", Norm(new Connection { Host = "h", Protocol = "ssh", Ssh = new SshOptions { JumpHost = "bob@bastion.corp:2222" } }).Ssh!.JumpHost);
    }

    [Fact]
    public void Ssh_arguments_end_options_with_double_dash_before_the_host()
    {
        var c = Norm(new Connection { Host = "srv01", Protocol = "ssh", Port = 2222, Username = "alice", Ssh = new SshOptions { JumpHost = "bastion" } });
        Assert.Equal(["-l", "alice", "-p", "2222", "-J", "bastion", "--", "srv01"], Launchers.SshArgs(c, null));
        Assert.Equal(["-o", "StrictHostKeyChecking=yes", "--", "srv01"], Launchers.SshArgs(c, "yes").TakeLast(4));
    }

    [Fact]
    public void The_cmd_command_line_contains_no_characters_cmd_interprets_in_values()
    {
        var c = Norm(new Connection { Host = "srv01", Protocol = "ssh", Username = "CORP\\alice" });
        var line = Launchers.SshCmdArguments(c, null);
        Assert.Matches("^/d /v:off /s /c \"\"", line);
        Assert.Matches(@"-l CORP\\alice -p 22 -- srv01 & if errorlevel 255", line);
        // Validation bypassed on purpose: the launcher still refuses.
        c.Ssh = new SshOptions { JumpHost = "a&calc" };
        Assert.Contains("not allowed", Assert.Throws<InvalidOperationException>(() => Launchers.SshArgs(c, null)).Message);
    }

    [Fact]
    public void Web_targets_are_http_only_and_built_from_fields()
    {
        var c = Norm(new Connection { Host = "ilo01.corp", Protocol = "web", Port = 8443, Web = new WebOptions { Path = "/admin?x=1" } });
        Assert.Equal("https://ilo01.corp:8443/admin?x=1", Launchers.WebTarget(c));
        Assert.Contains("path", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "web", Web = new WebOptions { Path = "javascript:alert(1)" } })).Message);
        Assert.Contains("path", Assert.Throws<ValidationException>(() => Norm(new Connection { Host = "h", Protocol = "web", Web = new WebOptions { Path = "/a b" } })).Message);
        Assert.Equal("https://[fe80::1]", HostRules.WebUrl("fe80::1", 443, new WebOptions()));
    }

    private static object[]? First(string s) => AddressParser.ParseTargets(s).FirstOrDefault() is { } t ? [t.Protocol, t.Host, t.Port, t.Username] : null;

    [Fact]
    public void Typed_addresses_are_detected()
    {
        Assert.Equal(["rdp", "srv01", 3389, ""], First("srv01"));
        Assert.Equal(["rdp", "ssh", "web"], AddressParser.ParseTargets("srv01").Select(t => t.Protocol));
        Assert.Equal(["ssh", "srv01", 22, ""], First("srv01:22"));
        Assert.Equal(["ssh", "srv01", 22, "alice"], First("alice@srv01"));
        Assert.Equal(["ssh", "srv01", 2222, "alice"], First("ssh -p 2222 alice@srv01"));
        Assert.Equal(["ssh", "10.0.0.5", 2200, "bob"], First("ssh://bob@10.0.0.5:2200"));
        Assert.Equal(["web", "ilo01", 443, ""], First("https://ilo01/admin"));
        Assert.Equal(["rdp", "srv01", 3389, ""], First("rdp srv01"));
        foreach (var bad in new[] { "-oProxyCommand=calc", "ssh -oProxyCommand=x host", "a;calc", "user$(x)@h", "file:///c:/x", "javascript:alert(1)" })
            Assert.Empty(AddressParser.ParseTargets(bad));
    }

    [Fact]
    public void Enabled_targets_fall_back_to_rdp_when_other_types_are_off()
    {
        var t = AddressParser.EnabledTargets("server01:22", ["rdp"]);
        Assert.Single(t);
        Assert.Equal(("rdp", "server01", 22), (t[0].Protocol, t[0].Host, t[0].Port));
        Assert.Equal(3390, AddressParser.EnabledTargets("10.20.30.40:3390", ["rdp"])[0].Port);
        Assert.Empty(AddressParser.EnabledTargets("bad host", ["rdp"]));
    }
}
