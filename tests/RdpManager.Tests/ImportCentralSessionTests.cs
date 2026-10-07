using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using RdpManager.Core.Central;
using RdpManager.Core.Import;
using RdpManager.Core.Models;
using RdpManager.Core.Rdp;
using RdpManager.Core.Sessions;
using RdpManager.Infrastructure.Central;
using static RdpManager.Tests.CoreTests;

namespace RdpManager.Tests;

/// <summary>Ported from test/iteration3.test.js: RDCMan, mRemoteNG, central list, session state machine.</summary>
public class ImportCentralSessionTests
{
    private const string Rdg = """
<?xml version="1.0" encoding="utf-8"?>
<RDCMan programVersion="2.90" schemaVersion="3"><file><credentialsProfiles/>
 <properties><expanded>True</expanded><name>BP</name></properties>
 <logonCredentials inherit="None"><profileName scope="Local">Custom</profileName><userName>jdoe</userName><password>AQAAANCMnd8B</password><domain>BPNET</domain></logonCredentials>
 <group><properties><name>Finance</name></properties>
  <gatewaySettings inherit="None"><enabled>True</enabled><hostName>rdgw.bp.example</hostName><localBypass>True</localBypass></gatewaySettings>
  <server><properties><displayName>Finance Prod</displayName><name>fin-prod.bp.example</name><comment>SAP</comment></properties>
   <connectionSettings inherit="None"><connectToConsole>True</connectToConsole><startProgram>cmd.exe</startProgram><port>3390</port></connectionSettings>
   <remoteDesktop inherit="None"><size>1600 x 900</size><fullScreen>False</fullScreen></remoteDesktop>
   <localResources inherit="None"><redirectClipboard>False</redirectClipboard><redirectDrives>True</redirectDrives><redirectPrinters>True</redirectPrinters></localResources>
  </server>
  <group><properties><name>Test</name></properties>
   <server><properties><name>fin-test.bp.example</name></properties><logonCredentials inherit="FromParent"/></server>
  </group>
 </group></file></RDCMan>
""";

    [Fact]
    public void RdcMan_maps_servers_groups_inheritance_and_drops_secrets()
    {
        var r = ConnectionImporters.ParseRdg(Rdg);
        Assert.Equal(2, r.Items.Count);
        var prod = r.Items[0];
        var test = r.Items[1];
        Assert.Equal("Finance Prod", prod.Connection.Name);
        Assert.Equal("fin-prod.bp.example", prod.Connection.Host);
        Assert.Equal(3390, prod.Connection.Port);
        Assert.Equal("Finance", prod.Connection.Folder);
        Assert.Equal("BPNET\\jdoe", prod.Connection.Username);
        Assert.True(prod.Connection.Security!.AdminSession);
        Assert.Equal(("detect", "rdgw.bp.example"), (prod.Connection.Gateway!.Mode, prod.Connection.Gateway.Host));
        Assert.Equal("window", prod.Connection.Display!.Mode);
        Assert.Equal(1600, prod.Connection.Display.Width);
        Assert.False(prod.Connection.Redirect!.Clipboard);
        Assert.Equal("none", prod.Connection.Redirect.Drives);
        Assert.True(prod.Connection.Redirect.Printers);
        Assert.Contains(prod.Warnings, w => w.Contains("password", StringComparison.Ordinal));
        Assert.Contains(prod.Warnings, w => w.Contains("start program", StringComparison.Ordinal));
        Assert.Contains(prod.Warnings, w => w.Contains("drive", StringComparison.Ordinal));
        Assert.DoesNotContain("AQAAANCMnd8B", JsonSerializer.Serialize(r.Items.Select(i => i.Connection).ToList(), Core.Json.RdpJsonContext.Default.ListConnection));
        Assert.Equal("Finance / Test", test.Connection.Folder);
        Assert.Equal("BPNET\\jdoe", test.Connection.Username);
        Assert.Equal("rdgw.bp.example", test.Connection.Gateway!.Host);
        Assert.NotEmpty(r.Warnings);
        foreach (var it in r.Items) Norm(it.Connection);
    }

    [Fact]
    public void Xml_import_rejects_dtds_and_unknown_formats()
    {
        Assert.Contains("document type", Assert.Throws<FormatException>(() => ConnectionImporters.DetectAndParse("<!DOCTYPE x [<!ENTITY a \"b\">]><RDCMan><file/></RDCMan>")).Message);
        Assert.Contains("not supported", Assert.Throws<FormatException>(() => ConnectionImporters.DetectAndParse("<foo/>")).Message);
        Assert.Throws<FormatException>(() => ConnectionImporters.DetectAndParse("<RDCMan><file><server>"));
    }

    private const string Mr = """
<?xml version="1.0" encoding="utf-8"?>
<Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" FullFileEncryption="false" ConfVersion="2.6">
  <Node Name="Servers" Type="Container" Username="admin" Domain="CORP" Port="3389">
    <Node Name="App 1" Type="Connection" Protocol="RDP" Hostname="app1.corp.example" Port="3391" Username="" InheritUsername="true" InheritDomain="true" Password="secretblob" Resolution="Res1280x720" RedirectClipboard="true" RedirectDiskDrives="true" RDGatewayUsageMethod="Always" RDGatewayHostname="gw.corp.example" UseConsoleSession="true" RDPAuthenticationLevel="AuthRequired" />
    <Node Name="Linux" Type="Connection" Protocol="SSH2" Hostname="linux.corp.example" Username="root" Password="sshsecret" />
    <Node Name="iLO" Type="Connection" Protocol="HTTPS" Hostname="ilo01.corp.example" Port="8443" />
    <Node Name="Lab VNC" Type="Connection" Protocol="VNC" Hostname="vnc.corp.example" />
  </Node>
</Connections>
""";

    [Fact]
    public void MRemoteNG_maps_rdp_ssh_https_resolves_inheritance_and_drops_passwords()
    {
        var all = ConnectionImporters.ParseMremote(Mr, ["rdp", "ssh", "web"]);
        Assert.Equal(3, all.Items.Count);
        var rdpOnly = ConnectionImporters.ParseMremote(Mr, ["rdp"]);
        Assert.Single(rdpOnly.Items);
        Assert.Contains(rdpOnly.Warnings, w => w.Contains("SSH2: 1") && w.Contains("HTTPS: 1"));
        var ssh = all.Items[1].Connection;
        Assert.Equal(("ssh", "linux.corp.example", 22, "root"), (ssh.Protocol, ssh.Host, ssh.Port, ssh.Username));
        var web = all.Items[2].Connection;
        Assert.Equal(("web", "ilo01.corp.example", 8443, "https"), (web.Protocol, web.Host, web.Port, web.Web!.Scheme));
        Assert.Contains(all.Warnings, w => w.Contains("VNC: 1"));
        var c = all.Items[0].Connection;
        Assert.Equal("app1.corp.example", c.Host);
        Assert.Equal(3391, c.Port);
        Assert.Equal("CORP\\admin", c.Username);
        Assert.Equal("Servers", c.Folder);
        Assert.Equal(1280, c.Display!.Width);
        Assert.Equal(("always", "gw.corp.example"), (c.Gateway!.Mode, c.Gateway.Host));
        Assert.Equal("none", c.Redirect!.Drives);
        Assert.Equal(1, c.Security!.AuthLevel);
        Assert.DoesNotContain("secretblob", JsonSerializer.Serialize(all.Items.Select(i => i.Connection).ToList(), Core.Json.RdpJsonContext.Default.ListConnection));
        Assert.Contains(all.Warnings, w => w.Contains("skipped"));
        Assert.Contains("encrypted", Assert.Throws<FormatException>(() => ConnectionImporters.ParseMremote("<Connections FullFileEncryption=\"true\"></Connections>")).Message);
    }

    // ── Central list ──────────────────────────────────────
    private static (Ed25519PrivateKeyParameters Priv, string PubSpkiB64) NewKey()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        var spki = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pair.Public).GetDerEncoded();
        return ((Ed25519PrivateKeyParameters)pair.Private, Convert.ToBase64String(spki));
    }

    private static (byte[] Bytes, string Sig) Signed(string json, Ed25519PrivateKeyParameters key)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var s = new Ed25519Signer();
        s.Init(true, key);
        s.BlockUpdate(bytes, 0, bytes.Length);
        return (bytes, Convert.ToBase64String(s.GenerateSignature()));
    }

    [Fact]
    public async Task Central_list_signature_rollback_protection_and_offline_cache()
    {
        var (priv, pub) = NewKey();
        var id = Guid.NewGuid().ToString();
        string Doc(int version) => $$"""{"schema":"bp-rdp-systems/1","version":{{version}},"systems":[{"id":"{{id}}","name":"Central A","host":"central-a.example"},{"id":"not-a-uuid","host":"x"}]}""";
        var v2 = Signed(Doc(2), priv);
        Assert.Equal(2, CentralListDocument.Verify(v2.Bytes, v2.Sig, pub).Version);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(v2.Bytes).Replace("central-a", "evil-host", StringComparison.Ordinal));
        Assert.Contains("signature", Assert.Throws<InvalidDataException>(() => CentralListDocument.Verify(tampered, v2.Sig, pub)).Message);
        Assert.Contains("public key", Assert.Throws<InvalidDataException>(() => CentralListDocument.Verify(v2.Bytes, v2.Sig, "AAAA")).Message);

        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "systems.json");
        File.WriteAllBytes(file, v2.Bytes);
        File.WriteAllText(file + ".sig", v2.Sig);
        var list = new CentralListService(new CentralListConfig { Path = file, PublicKey = pub }, Path.Combine(dir.Path, "cache"), () => new ConnectionDefaults());
        await list.RefreshAsync();
        Assert.Equal("current", list.Info.State);
        Assert.Single(list.Systems);
        Assert.Equal($"central-{id}", list.Systems[0].Id);
        Assert.Equal("central", list.Systems[0].Source);

        // An older version on the server is rejected; the cached v2 stays in use.
        var v1 = Signed(Doc(1), priv);
        File.WriteAllBytes(file, v1.Bytes);
        File.WriteAllText(file + ".sig", v1.Sig);
        await list.RefreshAsync();
        Assert.Equal("offline", list.Info.State);
        Assert.Equal(2, list.Info.Version);
        Assert.Contains("older", list.Info.Error);

        // Source unavailable: the cached copy is used.
        File.Delete(file);
        await list.RefreshAsync();
        Assert.Equal("offline", list.Info.State);
        Assert.Single(list.Systems);
    }

    [Fact]
    public void Expired_central_lists_are_rejected()
    {
        var (priv, pub) = NewKey();
        var doc = Signed("""{"schema":"bp-rdp-systems/1","version":3,"expiresAt":"2020-01-01T00:00:00Z","systems":[]}""", priv);
        Assert.Contains("expired", Assert.Throws<InvalidDataException>(() => CentralListDocument.Verify(doc.Bytes, doc.Sig, pub)).Message);
    }

    // ── Session state machine ─────────────────────────────
    private static TrackedSession Fake(SessionState state) => new(new SessionInfo
    {
        Id = "S", ConnectionId = "C", Name = "n", Host = "h", Pid = 42, State = state, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
    });

    private static RdpClientEvent Ev(int id, int? reason = null)
        => new(id, 42, DateTimeOffset.UtcNow, reason is null ? [] : ["Disconnect Reason", reason.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "Info"]);

    [Fact]
    public void Session_state_machine()
    {
        var now = DateTimeOffset.UtcNow;
        var s = Fake(SessionState.Connecting);
        var a = MstscSessionStateMachine.Apply(s, [Ev(1024), Ev(1027)], now);
        Assert.Equal(SessionState.Active, s.Info.State);
        Assert.True(s.Info.Verified);
        Assert.True(a.HasFlag(SessionAction.RemoveConnectionFile));

        s = Fake(SessionState.Active);
        MstscSessionStateMachine.Apply(s, [Ev(1027), Ev(1026, 1)], now);
        Assert.Equal(SessionState.Active, s.Info.State);
        MstscSessionStateMachine.Apply(s, [Ev(1027), Ev(1026, 0x904)], now);
        Assert.Equal(SessionState.Reconnecting, s.Info.State);
        MstscSessionStateMachine.Apply(s, [Ev(1027), Ev(1026, 0x904), Ev(1027)], now);
        Assert.Equal(SessionState.Active, s.Info.State);

        s = Fake(SessionState.Connecting);
        a = MstscSessionStateMachine.Apply(s, [Ev(1024), Ev(1026, 0x807)], now);
        Assert.Equal(SessionState.Connecting, s.Info.State);
        Assert.True(s.Info.Result!.Hint);
        Assert.False(a.HasFlag(SessionAction.KillClient));

        s = Fake(SessionState.Connecting);
        a = MstscSessionStateMachine.Apply(s, [Ev(1024), Ev(1026, 0x104)], now);
        Assert.Equal(SessionState.Failed, s.Info.State);
        Assert.True(a.HasFlag(SessionAction.KillClient));

        s = Fake(SessionState.Active);
        s.Info.Verified = false;
        MstscSessionStateMachine.Apply(s, [Ev(1027)], now);
        Assert.True(s.Info.Verified);

        // Without an event log a live client counts as active after the grace period (unverified).
        s = Fake(SessionState.Connecting);
        MstscSessionStateMachine.Apply(s, null, now.AddMinutes(5));
        Assert.Equal(SessionState.Active, s.Info.State);
        Assert.False(s.Info.Verified);
    }

    [Fact]
    public void Session_exit_results()
    {
        var s = Fake(SessionState.Connecting);
        s.UserDisconnect = true;
        MstscSessionStateMachine.OnExit(s, [], DateTimeOffset.UtcNow);
        Assert.Equal("Connection cancelled", s.Info.Result!.Title);

        s = Fake(SessionState.Active);
        MstscSessionStateMachine.OnExit(s, [Ev(1026, 0x807)], DateTimeOffset.UtcNow);
        Assert.Equal(SessionState.Failed, s.Info.State);

        s = Fake(SessionState.Active);
        MstscSessionStateMachine.OnExit(s, [Ev(1026, 0x904), Ev(1027)], DateTimeOffset.UtcNow);
        Assert.Equal(SessionState.Ended, s.Info.State);
        Assert.Equal("Session closed", s.Info.Result!.Title);
    }
}

/// <summary>A temporary folder that is deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rdpmanager-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
