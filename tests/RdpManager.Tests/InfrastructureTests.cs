using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Policy;
using RdpManager.Infrastructure.Credentials;
using RdpManager.Infrastructure.Logging;
using RdpManager.Infrastructure.Migration;
using RdpManager.Infrastructure.Network;
using RdpManager.Infrastructure.Rdp;
using RdpManager.Infrastructure.Security;
using RdpManager.Infrastructure.Storage;

namespace RdpManager.Tests;

/// <summary>Persistence, migration of Electron data, policy, probes, Credential Manager and log redaction.</summary>
public class InfrastructureTests
{
    /// <summary>A connections.json exactly as the Electron app 0.4.0 writes it (with an extra unknown field).</summary>
    internal const string ElectronConnections = """
[
  {
    "id": "db7df6c8-0d17-4f12-8457-ad8196e1ee41",
    "name": "Test",
    "protocol": "rdp",
    "host": "192.168.1.200",
    "port": 3389,
    "username": "Administrator",
    "folder": "",
    "os": "",
    "location": "",
    "tags": [],
    "description": "",
    "favorite": false,
    "sample": false,
    "credentialMode": "prompt",
    "ssh": { "identityFile": "", "jumpHost": "" },
    "web": { "scheme": "https", "path": "" },
    "display": { "mode": "fullscreen", "width": 1920, "height": 1080, "multimon": false, "dynamicResolution": true, "smartSizing": false },
    "redirect": { "clipboard": true, "drives": "none", "printers": false, "audio": "local", "microphone": false, "smartcards": false },
    "gateway": { "mode": "none", "host": "" },
    "security": { "adminSession": false, "credentialProtection": "none", "authLevel": 2 },
    "lastConnectedAt": "2026-10-06T06:26:34.676Z",
    "createdAt": "2026-10-06T06:25:37.964Z",
    "futureField": { "keep": true }
  },
  {
    "id": "0f6b4f3a-1111-4222-8333-944455556666",
    "name": "Second",
    "host": "srv02.corp.local",
    "port": 3390,
    "favorite": true,
    "tags": ["sap", "finance"]
  },
  {
    "id": "not-a-valid-id",
    "host": "bad host name"
  }
]
""";

    private static AppPaths WithElectronData(TempDir dir, string connections = ElectronConnections)
    {
        var paths = AppPaths.ForTest(dir.Path);
        Directory.CreateDirectory(paths.LegacyDataDir);
        File.WriteAllText(Path.Combine(paths.LegacyDataDir, "connections.json"), connections);
        File.WriteAllText(Path.Combine(paths.LegacyDataDir, "settings.json"), """{"launchMode":"direct","statusRefreshSeconds":120,"showConnectDialog":true,"defaults":{"redirect":{"clipboard":false}}}""");
        File.WriteAllText(Path.Combine(paths.LegacyDataDir, "audit.log"), "{\"ts\":\"2026-10-06T06:26:36.538Z\",\"event\":\"session_ended\",\"host\":\"h\",\"state\":\"ended\"}\n");
        return paths;
    }

    [Fact]
    public void Migration_backs_up_validates_and_imports_electron_data()
    {
        using var dir = new TempDir();
        var paths = WithElectronData(dir);
        var before = File.ReadAllBytes(Path.Combine(paths.LegacyDataDir, "connections.json"));
        var record = new ElectronDataMigrator(paths).RunIfNeeded()!;

        Assert.Equal(ElectronDataMigrator.Completed, record.Status);
        Assert.Equal(2, record.Imported);
        Assert.Equal(1, record.Rejected);
        Assert.True(record.SettingsImported);
        Assert.True(record.AuditImported);
        // The source is never changed; the backup is complete.
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(paths.LegacyDataDir, "connections.json")));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(record.Backup, "connections.json")));
        Assert.True(File.Exists(Path.Combine(record.Backup, "settings.json")));
        Assert.True(File.Exists(paths.MigrationRejectedFile));

        var store = new DataStore(paths);
        store.Load();
        Assert.Equal(2, store.Connections.Count);
        var first = store.Get("db7df6c8-0d17-4f12-8457-ad8196e1ee41")!;
        Assert.Equal("2026-10-06T06:26:34.676Z", first.LastConnectedAt);
        Assert.Equal("2026-10-06T06:25:37.964Z", first.CreatedAt);
        Assert.True(first.Extra!.ContainsKey("futureField"), "unknown fields are preserved");
        Assert.Equal("direct", store.Settings.LaunchMode);
        Assert.Equal(120, store.Settings.StatusRefreshSeconds);
        Assert.False(store.Settings.Defaults.Redirect.Clipboard);
        Assert.Single(store.Audit.ReadRecent());
    }

    [Fact]
    public async Task Migration_is_not_repeated_and_a_rerun_merges_without_duplicates()
    {
        using var dir = new TempDir();
        var paths = WithElectronData(dir);
        var migrator = new ElectronDataMigrator(paths);
        migrator.RunIfNeeded();
        // A system added in the native app must survive a repeated import.
        var store = new DataStore(paths);
        store.Load();
        store.Save(new Connection { Name = "Native only", Host = "native.corp" });
        await store.FlushAsync();

        Assert.Equal(ElectronDataMigrator.Completed, migrator.RunIfNeeded()!.Status);
        var again = migrator.Run();
        Assert.Equal(0, again.Imported);
        Assert.Equal(2, again.Merged);
        store.Load();
        Assert.Equal(3, store.Connections.Count);
        Assert.Contains(store.Connections, c => c.Name == "Native only");
    }

    [Fact]
    public void Migration_reports_a_damaged_source_and_keeps_a_backup()
    {
        using var dir = new TempDir();
        var paths = WithElectronData(dir, "{ this is not json");
        var record = new ElectronDataMigrator(paths).RunIfNeeded()!;
        Assert.Equal(ElectronDataMigrator.Failed, record.Status);
        Assert.Contains("damaged", record.Error);
        Assert.True(File.Exists(Path.Combine(record.Backup, "connections.json")));
        Assert.False(File.Exists(paths.ConnectionsFile));
    }

    [Fact]
    public void Migration_does_nothing_without_electron_data()
    {
        using var dir = new TempDir();
        Assert.Null(new ElectronDataMigrator(AppPaths.ForTest(dir.Path)).RunIfNeeded());
    }

    [Fact]
    public async Task Data_store_round_trip_writes_electron_compatible_json()
    {
        using var dir = new TempDir();
        var paths = AppPaths.ForTest(dir.Path);
        var store = new DataStore(paths);
        store.Load();
        var saved = store.Save(new Connection { Name = "Srv", Host = "srv.corp", Tags = ["a"] });
        store.Patch(saved.Id, c => c.Favorite = true);
        store.PatchCentral("central-" + Guid.NewGuid(), favorite: true);
        store.SaveSettings(new AppSettings { StatusRefreshSeconds = 300 });
        await store.FlushAsync();

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.ConnectionsFile));
        var item = doc.RootElement[0];
        Assert.Equal("srv.corp", item.GetProperty("host").GetString());
        Assert.Equal(3389, item.GetProperty("port").GetInt32());
        Assert.True(item.GetProperty("favorite").GetBoolean());
        Assert.True(item.TryGetProperty("createdAt", out _));
        Assert.False(item.TryGetProperty("source", out _));
        Assert.EndsWith("Z", item.GetProperty("createdAt").GetString());

        var reloaded = new DataStore(paths);
        reloaded.Load();
        Assert.Single(reloaded.Connections);
        Assert.Equal(300, reloaded.Settings.StatusRefreshSeconds);
        Assert.Empty(Directory.GetFiles(paths.DataDir, "*.tmp"));
        Assert.True(reloaded.Delete(saved.Id));
    }

    [Fact]
    public void A_damaged_connections_file_is_moved_aside_not_overwritten()
    {
        using var dir = new TempDir();
        var paths = AppPaths.ForTest(dir.Path);
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.ConnectionsFile, "[ broken");
        var store = new DataStore(paths);
        store.Load();
        Assert.NotNull(store.Corrupt);
        Assert.Empty(store.Connections);
        Assert.Equal("[ broken", File.ReadAllText(store.Corrupt!.Backup));
    }

    [Fact]
    public async Task Audit_log_writes_json_lines_without_secrets_and_reads_newest_first()
    {
        using var dir = new TempDir();
        var log = new AuditLog(Path.Combine(dir.Path, "audit.log"), dir.Path);
        log.Write("first", new Dictionary<string, object?> { ["host"] = "a", ["password"] = "secret!" });
        log.Write("second", new Dictionary<string, object?> { ["host"] = "b", ["redirect"] = new Dictionary<string, object?> { ["clipboard"] = true } });
        await log.FlushAsync();
        var text = File.ReadAllText(Path.Combine(dir.Path, "audit.log"));
        Assert.DoesNotContain("secret!", text);
        var entries = log.ReadRecent();
        Assert.Equal(["second", "first"], entries.Select(e => e.Event));
        Assert.Equal(AuditLog.CurrentUser, entries[0].User);
    }

    [Fact]
    public void Policy_parser_takes_only_known_keys_with_the_right_types()
    {
        var p = PolicyParser.Parse("""
        {
          "allowedGroups": ["CONTOSO\\BP-RDP-Users", 5],
          "allowSavedCredentials": false,
          "launchMode": "direct",
          "requireCredentialProtection": "remoteGuard",
          "redirect": { "drives": "none", "clipboard": true, "audio": "loud" },
          "helpUrl": "http://not-https.example",
          "allowedProtocols": ["rdp", "vnc"],
          "notice": { "message": "Maintenance" },
          "centralList": { "url": "https://rdp-config.example.com/systems.json", "publicKey": "AAA", "maxAgeHours": 24 },
          "entra": { "tenantId": "11111111-2222-3333-4444-555555555555", "clientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "requireSignIn": true, "allowedRoles": ["RDP.User"] },
          "unknown": 1
        }
        """, "policy.json");
        Assert.True(p.Active);
        Assert.Equal(["CONTOSO\\BP-RDP-Users"], p.AllowedGroups);
        Assert.False(p.AllowSavedCredentials);
        Assert.Equal("direct", p.LaunchMode);
        Assert.Equal("none", p.Redirect!.Drives);
        Assert.True(p.Redirect.Clipboard);
        Assert.Null(p.Redirect.Audio);
        Assert.Null(p.HelpUrl);
        Assert.Equal(["rdp"], p.AllowedProtocols);
        Assert.Equal("Notice from IT", p.Notice!.Title);
        Assert.Equal(24, p.CentralList!.MaxAgeHours);
        Assert.True(p.Entra!.RequireSignIn);
        Assert.Equal(["User.Read"], p.Entra.Scopes);
        Assert.Null(PolicyParser.Parse("""{"entra":{"tenantId":"x","clientId":"y"}}""", "f").Entra);
        Assert.False(PolicyParser.Parse("not json", "f").Active);
    }

    [Fact]
    public void Group_matching_accepts_full_and_short_names()
    {
        Assert.True(GroupAccessChecker.Matches(["CONTOSO\\BP-RDP-Users"], ["bp-rdp-users"]));
        Assert.True(GroupAccessChecker.Matches(["CONTOSO\\BP-RDP-Users"], ["contoso\\bp-rdp-users"]));
        Assert.False(GroupAccessChecker.Matches(["CONTOSO\\Other"], ["BP-RDP-Users"]));
        Assert.True(GroupAccessChecker.Check(null).Allowed);
    }

    [Fact]
    public async Task Probe_reports_reachable_refused_and_dns()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ok = await ReachabilityProbe.ProbeAsync("127.0.0.1", port, 2000);
        Assert.True(ok.Reachable);
        listener.Stop();
        var refused = await ReachabilityProbe.ProbeAsync("127.0.0.1", port, 5000); // Windows retries a refused SYN for about 2 s
        Assert.False(refused.Reachable);
        Assert.Equal("refused", refused.Reason);
        var dns = await ReachabilityProbe.ProbeAsync("does-not-exist.invalid", 3389, 3000);
        Assert.Equal("dns", dns.Reason);
        var many = await ReachabilityProbe.ProbeManyAsync([new ProbeTarget("a", "does-not-exist.invalid", 3389), new ProbeTarget("b", "127.0.0.1", port)]);
        Assert.Equal(2, many.Count);
    }

    [Fact]
    public void Credential_manager_round_trip_never_returns_the_password()
    {
        var store = new WindowsCredentialStore();
        var host = $"rdpmanager-test-{Guid.NewGuid():N}.invalid";
        try
        {
            Assert.False(store.Get(host).Saved);
            store.Save(host, "CORP\\tester", "Pa55-word!");
            var cred = store.Get(host);
            Assert.True(cred.Saved);
            Assert.Equal("CORP\\tester", cred.Username);
            Assert.Throws<ArgumentException>(() => store.Save(host, "", "x"));
        }
        finally
        {
            store.Delete(host);
        }
        Assert.False(store.Get(host).Saved);
        Assert.False(store.Delete(host));
    }

    [Fact]
    public void Log_redaction_removes_secrets()
    {
        Assert.Equal("password=*** user=bob", DiagnosticLog.Redact("password=hunter2 user=bob"));
        Assert.Equal("Authorization: Bearer ***", DiagnosticLog.Redact("Authorization: Bearer eyJhbGciOi.abc.def"));
        Assert.Equal("code 0x904", DiagnosticLog.Redact("code 0x904"));
    }

    [Fact]
    public void Event_log_query_handles_unknown_processes()
    {
        Assert.Empty(RdpClientEventLog.Query(DateTimeOffset.UtcNow, [])!);
        var r = RdpClientEventLog.Query(DateTimeOffset.UtcNow.AddMinutes(-1), [999999]);
        Assert.True(r is null || r.Count == 0);
    }

    [Fact]
    public void Json_context_accepts_numbers_as_strings_like_central_lists_use()
    {
        var c = JsonSerializer.Deserialize("""{"host":"x","port":"3390"}""", RdpJsonContext.Default.Connection)!;
        Assert.Equal(3390, c.Port);
    }
}
