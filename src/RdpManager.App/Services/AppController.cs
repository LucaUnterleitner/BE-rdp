using RdpManager.App.Sessions;
using RdpManager.Core.Import;
using RdpManager.Core.Models;
using RdpManager.Core.Rdp;
using RdpManager.Core.Samples;
using RdpManager.Core.Targets;
using RdpManager.Core.Validation;
using RdpManager.Infrastructure.Auth;
using RdpManager.Infrastructure.Central;
using RdpManager.Infrastructure.Credentials;
using RdpManager.Infrastructure.Launchers;
using RdpManager.Infrastructure.Logging;
using RdpManager.Infrastructure.Network;
using RdpManager.Infrastructure.Security;
using RdpManager.Infrastructure.Sessions;
using RdpManager.Infrastructure.Storage;

namespace RdpManager.App.Services;

public sealed record ConnectStep(string ConnectionId, string Step, string State, string? Detail = null);

public sealed class ConnectOverrides
{
    public DisplayOptions? Display { get; init; }
    public RedirectOptions? Redirect { get; init; }
    public GatewayOptions? Gateway { get; init; }
    public SecurityOptions? Security { get; init; }
    public string? Username { get; init; }
    public bool PromptAlways { get; init; }
}

public abstract record ConnectOutcome;
public sealed record AlreadyActiveOutcome(SessionInfo Session) : ConnectOutcome;
public sealed record InProgressOutcome : ConnectOutcome;
public sealed record UnreachableOutcome(string Title, string Message, string Technical) : ConnectOutcome;
public sealed record StartedOutcome(SessionInfo Session, bool CredentialSaved, string? LaunchNote) : ConnectOutcome;
public sealed record OpenedOutcome(string Url) : ConnectOutcome;

/// <summary>One file or entry of an import, for the preview.</summary>
public sealed record ImportPreviewItem(string File, Connection? Connection, IReadOnlyList<string> Warnings, string? Error = null, string? Notice = null);

/// <summary>
/// Application flows (port of the IPC handlers and the connect flow of main.js). The UI calls this class with
/// connection ids; hosts and credentials are resolved here, never taken from the UI as free text.
/// </summary>
public sealed class AppController(
    AppPaths paths,
    DataStore store,
    AppPolicy policy,
    SessionManager sessions,
    ICredentialStore credentials,
    CentralListService central,
    IAuthService auth,
    SessionTabManager tabs)
{
    private readonly HashSet<string> _inFlight = [];
    private readonly Dictionary<string, Connection> _adhoc = [];
    private readonly Dictionary<string, ProbeResult> _status = [];
    private readonly object _gate = new();

    public AppPolicy Policy => policy;
    public AppPaths Paths => paths;
    public DataStore Store => store;
    public SessionManager Sessions => sessions;
    public CentralListService Central => central;
    public IAuthService Auth => auth;
    public AccessResult Access { get; set; } = AccessResult.Granted;

    /// <summary>Raised (any thread) when the list of systems changed.</summary>
    public event Action? ConnectionsChanged;
    /// <summary>Raised (any thread) with new reachability results.</summary>
    public event Action<IReadOnlyDictionary<string, ProbeResult>>? StatusChanged;
    public event Action<ConnectStep>? ConnectProgress;
    /// <summary>Raised when the recent systems changed (jump list, tray).</summary>
    public event Action? RecentChanged;

    // ── Systems ──────────────────────────────────────────
    public static bool IsCentral(string? id) => ConnectionNormalizer.IsCentralId(id);

    /// <summary>Personal systems plus the read-only central list (with personal favorites and last use).</summary>
    public List<Connection> AllConnections()
    {
        var list = store.Connections.ToList();
        foreach (var c in central.Systems)
        {
            var copy = c.Clone();
            var (fav, last) = store.CentralOverlay(c.Id);
            copy.Favorite = fav;
            copy.LastConnectedAt = last;
            list.Add(copy);
        }
        return list;
    }

    public Connection ConnectionOrThrow(string id)
    {
        if (IsCentral(id))
        {
            var c = central.Get(id)?.Clone() ?? throw new InvalidOperationException("System not found.");
            var (fav, last) = store.CentralOverlay(id);
            c.Favorite = fav;
            c.LastConnectedAt = last;
            return c;
        }
        lock (_gate) if (_adhoc.TryGetValue(id, out var a)) return a.Clone();
        return store.Get(id)?.Clone() ?? throw new InvalidOperationException("System not found.");
    }

    public IReadOnlyDictionary<string, ProbeResult> StatusSnapshot() { lock (_gate) return new Dictionary<string, ProbeResult>(_status); }

    public void AssertAllowed()
    {
        if (!Access.Allowed) throw new InvalidOperationException(Access.Reason ?? "You do not have access to this app.");
    }

    private static void AssertPersonal(string? id)
    {
        if (IsCentral(id)) throw new InvalidOperationException("This system is managed by IT and cannot be changed here. You can duplicate it as a personal system.");
    }

    /// <summary>Connection types enabled in this app version and allowed by IT on this computer.</summary>
    public bool ProtocolAllowed(string? protocol)
    {
        var p = string.IsNullOrEmpty(protocol) ? Protocols.Rdp : protocol;
        return Protocols.Enabled.Contains(p) && (policy.AllowedProtocols is null || policy.AllowedProtocols.Contains(p));
    }

    private void AssertProtocolAllowed(string? protocol)
    {
        var p = string.IsNullOrEmpty(protocol) ? Protocols.Rdp : protocol;
        if (!Protocols.Enabled.Contains(p)) throw new InvalidOperationException($"{Protocols.Label(p)} connections are not available in this app version.");
        if (!ProtocolAllowed(p)) throw new InvalidOperationException($"{Protocols.Label(p)} connections are switched off by IT policy.");
    }

    public Connection Save(Connection input)
    {
        AssertAllowed();
        var isNew = string.IsNullOrEmpty(input.Id) || store.Get(input.Id) is null;
        if (!string.IsNullOrEmpty(input.Id)) AssertPersonal(input.Id);
        AssertProtocolAllowed(input.Protocol);
        var saved = store.Save(input);
        store.Audit.Write(isNew ? "system_added" : "system_updated", new Dictionary<string, object?> { ["connectionId"] = saved.Id, ["name"] = saved.Name, ["host"] = saved.Host });
        ConnectionsChanged?.Invoke();
        RecentChanged?.Invoke();
        _ = RefreshStatusAsync([saved.Id]);
        return saved;
    }

    public void Delete(string id)
    {
        AssertAllowed();
        AssertPersonal(id);
        var c = ConnectionOrThrow(id);
        store.Delete(id);
        lock (_gate) _status.Remove(id);
        store.Audit.Write("system_removed", new Dictionary<string, object?> { ["connectionId"] = id, ["name"] = c.Name, ["host"] = c.Host });
        ConnectionsChanged?.Invoke();
        RecentChanged?.Invoke();
    }

    public bool ToggleFavorite(string id)
    {
        AssertAllowed();
        var c = ConnectionOrThrow(id);
        if (IsCentral(id)) store.PatchCentral(id, favorite: !c.Favorite);
        else store.Patch(id, x => x.Favorite = !c.Favorite);
        ConnectionsChanged?.Invoke();
        return !c.Favorite;
    }

    public int LoadSamples()
    {
        AssertAllowed();
        var existing = store.Connections.Select(c => c.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = store.SaveMany(SampleSystems.Create().Where(s => !existing.Contains(s.Host)));
        if (added.Count > 0) ConnectionsChanged?.Invoke();
        _ = RefreshStatusAsync(added.Select(a => a.Id).ToList());
        return added.Count;
    }

    // ── Import / export ──────────────────────────────────
    /// <summary>Reads and validates files for the import preview (runs off the UI thread).</summary>
    public List<ImportPreviewItem> ReadImportFiles(IEnumerable<string> files)
    {
        AssertAllowed();
        var defaults = store.Settings.Defaults;
        var items = new List<ImportPreviewItem>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var size = new FileInfo(file).Length;
                if (name.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                {
                    if (size > 256 * 1024) throw new InvalidDataException("The file is too large to be a Remote Desktop file.");
                    var r = RdpFile.ToConnection(RdpFile.Decode(File.ReadAllBytes(file)), name);
                    ConnectionNormalizer.Normalize(r.Connection, defaults);
                    items.Add(new ImportPreviewItem(name, r.Connection, r.Warnings));
                    continue;
                }
                if (size > 5 * 1024 * 1024) throw new InvalidDataException("The file is too large.");
                var parsed = ConnectionImporters.DetectAndParse(RdpFile.Decode(File.ReadAllBytes(file)));
                if (parsed.Items.Count == 0) throw new InvalidDataException($"No Remote Desktop connections were found in this {parsed.Format} file.");
                if (parsed.Warnings.Count > 0) items.Add(new ImportPreviewItem(name, null, [], Notice: $"{parsed.Format}: {string.Join(' ', parsed.Warnings)}"));
                foreach (var it in parsed.Items)
                {
                    try
                    {
                        ConnectionNormalizer.Normalize(it.Connection, defaults);
                        items.Add(new ImportPreviewItem(name, it.Connection, it.Warnings));
                    }
                    catch (ValidationException e)
                    {
                        var label = it.Connection.Name.Length > 0 ? it.Connection.Name : it.Connection.Host.Length > 0 ? it.Connection.Host : "entry";
                        items.Add(new ImportPreviewItem($"{name}: {label}", null, [], e.Message));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ValidationException or OverflowException)
            {
                items.Add(new ImportPreviewItem(name, null, [], e.Message));
            }
        }
        return items.Take(1000).ToList();
    }

    public int ConfirmImport(IEnumerable<Connection> drafts)
    {
        AssertAllowed();
        var saved = store.SaveMany(drafts.Take(1000).Select(d =>
        {
            var c = d.Clone();
            c.Id = "";
            c.Favorite = false;
            c.Sample = false;
            return c;
        }));
        store.Audit.Write("systems_imported", new Dictionary<string, object?> { ["count"] = saved.Count, ["hosts"] = saved.Select(s => s.Host).Take(200).ToList() });
        ConnectionsChanged?.Invoke();
        _ = RefreshStatusAsync(saved.Select(s => s.Id).ToList());
        return saved.Count;
    }

    public void ExportRdp(string id, string file)
    {
        AssertAllowed();
        var c = ConnectionOrThrow(id);
        if (Protocols.Of(c) != Protocols.Rdp) throw new InvalidOperationException("Only Remote Desktop systems can be exported as .rdp files.");
        File.WriteAllBytes(file, RdpFile.Encode(RdpFile.Build(RdpFile.ApplyPolicy(c, policy))));
        store.Audit.Write("system_exported", new Dictionary<string, object?> { ["connectionId"] = c.Id, ["name"] = c.Name, ["host"] = c.Host });
    }

    // ── Settings ─────────────────────────────────────────
    public AppSettings SaveSettings(AppSettings input)
    {
        var s = store.SaveSettings(input);
        store.Audit.Write("settings_saved", new Dictionary<string, object?> { ["launchMode"] = s.LaunchMode, ["signed"] = s.SigningThumbprint.Length > 0 });
        return s;
    }

    /// <summary>Launch mode after IT policy (port of effectiveLaunch).</summary>
    public (string LaunchMode, string SigningThumbprint, string? Note) EffectiveLaunch(AppSettings settings)
    {
        var mode = policy.LaunchMode ?? settings.LaunchMode;
        string? note = null;
        // Direct launch cannot enforce redirection locks (mstsc reads them from Default.rdp).
        if (mode == "direct" && policy.Redirect is { Any: true })
        {
            mode = "file";
            note = "IT policy locks device options, so connections use a connection file.";
        }
        return (mode, policy.SigningThumbprint ?? settings.SigningThumbprint, note);
    }

    // ── Credentials (addressed by connection id) ─────────
    public CredentialInfo GetCredential(string id) => credentials.Get(ConnectionOrThrow(id).Host);

    public void SaveCredential(string id, string username, string password)
    {
        AssertAllowed();
        if (policy.AllowSavedCredentials == false) throw new InvalidOperationException("Saving passwords is switched off by IT policy.");
        var c = ConnectionOrThrow(id);
        credentials.Save(c.Host, username.Trim(), password);
        store.Audit.Write("credential_saved", new Dictionary<string, object?> { ["connectionId"] = id, ["host"] = c.Host, ["username"] = username.Trim() });
    }

    public bool DeleteCredential(string id)
    {
        AssertAllowed();
        var c = ConnectionOrThrow(id);
        var removed = credentials.Delete(c.Host);
        store.Audit.Write("credential_removed", new Dictionary<string, object?> { ["connectionId"] = id, ["host"] = c.Host });
        return removed;
    }

    // ── Status ───────────────────────────────────────────
    /// <summary>Probes the gateway (443) when the connection always uses one, otherwise the host.</summary>
    public static ProbeTarget ProbeTargetOf(Connection c)
    {
        if (Protocols.Of(c) == Protocols.Rdp && c.Gateway is { Mode: "always" } g && g.Host.Length > 0) return new ProbeTarget(c.Id, g.Host, 443, true);
        return new ProbeTarget(c.Id, c.Host, c.Port == 0 ? 3389 : c.Port);
    }

    public DateTimeOffset LastFullStatusAt { get; private set; }

    public async Task RefreshStatusAsync(IReadOnlyCollection<string>? ids = null, CancellationToken ct = default)
    {
        if (!Access.Allowed) return;
        if (ids is null) LastFullStatusAt = DateTimeOffset.UtcNow;
        var targets = AllConnections().Where(c => ids is null || ids.Contains(c.Id)).Select(ProbeTargetOf).ToList();
        if (targets.Count == 0) return;
        var results = await ReachabilityProbe.ProbeManyAsync(targets, 6, ct).ConfigureAwait(false);
        lock (_gate) foreach (var (k, v) in results) _status[k] = v;
        StatusChanged?.Invoke(results);
    }

    public async Task<ProbeResult> ProbeAsync(string id)
    {
        var t = ProbeTargetOf(ConnectionOrThrow(id));
        var r = await ReachabilityProbe.ProbeAsync(t.Host, t.Port).ConfigureAwait(false);
        lock (_gate) _status[id] = r;
        StatusChanged?.Invoke(new Dictionary<string, ProbeResult> { [id] = r });
        return r;
    }

    public async Task RefreshCentralAsync()
    {
        await central.RefreshAsync().ConfigureAwait(false);
        ConnectionsChanged?.Invoke();
        RecentChanged?.Invoke();
        _ = RefreshStatusAsync(central.Systems.Select(c => c.Id).ToList());
    }

    // ── Quick connect ────────────────────────────────────
    /// <summary>Host name or IP (optionally :port) without creating a system first, or saved when asked.</summary>
    public Connection QuickTarget(string address, string? protocol, bool save)
    {
        AssertAllowed();
        var candidates = AddressParser.EnabledTargets(address);
        var t = candidates.FirstOrDefault(c => c.Protocol == protocol) ?? candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("Enter a computer name or IP address.");
        AssertProtocolAllowed(t.Protocol);
        var fields = new Connection
        {
            Name = t.Host, Protocol = t.Protocol, Host = t.Host, Port = t.Port, Username = t.Username,
            Web = new WebOptions { Scheme = t.Scheme, Path = t.Path },
        };
        if (save)
        {
            var existing = store.Connections.FirstOrDefault(c => Protocols.Of(c) == t.Protocol && string.Equals(c.Host, t.Host, StringComparison.OrdinalIgnoreCase) && c.Port == t.Port);
            if (existing is not null) return existing;
            var saved = store.Save(fields);
            store.Audit.Write("system_added", new Dictionary<string, object?> { ["connectionId"] = saved.Id, ["name"] = saved.Name, ["host"] = saved.Host, ["via"] = "quick connect" });
            ConnectionsChanged?.Invoke();
            return saved;
        }
        var conn = ConnectionNormalizer.Normalize(fields, store.Settings.Defaults);
        conn.Adhoc = true;
        lock (_gate) _adhoc[conn.Id] = conn;
        return conn.Clone();
    }

    // ── Connect ──────────────────────────────────────────
    /// <summary>Connection flow with visible steps: availability, settings, credentials, start.</summary>
    public async Task<ConnectOutcome> ConnectAsync(string id, ConnectOverrides? overrides = null, bool force = false, bool quick = false)
    {
        AssertAllowed();
        // Enforced here (not only in the UI), so jump list, tray and "--connect" cannot bypass it.
        if (auth.RequireSignIn && !auth.State.SignedIn) throw new InvalidOperationException("Sign in with your work account first.");
        if (auth.RequireSignIn && !auth.HasRequiredRole(auth.State)) throw new InvalidOperationException("Your account does not have a role that may use this app. Ask IT for access.");
        var baseConn = ConnectionOrThrow(id);
        var running = sessions.ActiveFor(id);
        if (running is not null) return new AlreadyActiveOutcome(running);
        lock (_gate)
        {
            if (!_inFlight.Add(id)) return new InProgressOutcome();
        }
        try
        {
            return await ConnectStepsAsync(id, baseConn, overrides, force, quick).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _inFlight.Remove(id);
        }
    }

    private void Progress(string id, string step, string state, string? detail = null) => ConnectProgress?.Invoke(new ConnectStep(id, step, state, detail));

    private async Task<ConnectOutcome> ConnectStepsAsync(string id, Connection baseConn, ConnectOverrides? o, bool force, bool quick)
    {
        // Only known option groups may change for one connection; host, port and id stay as saved.
        var input = baseConn.Clone();
        if (o?.Display is not null) input.Display = o.Display;
        if (o?.Redirect is not null) input.Redirect = o.Redirect;
        if (o?.Gateway is not null) input.Gateway = o.Gateway;
        if (o?.Security is not null) input.Security = o.Security;
        if (o?.Username is not null) input.Username = o.Username;
        var merged = ConnectionNormalizer.Normalize(input, store.Settings.Defaults);
        merged.Id = baseConn.Id;
        AssertProtocolAllowed(merged.Protocol);
        if (merged.Protocol == Protocols.Web) return OpenWeb(id, merged);
        var conn = merged.Protocol == Protocols.Rdp ? RdpFile.ApplyPolicy(merged, policy) : merged;
        conn.PromptAlways = o?.PromptAlways == true;

        // 1. Availability
        Progress(id, "check", "running");
        var target = ProbeTargetOf(conn);
        var result = await ReachabilityProbe.ProbeAsync(target.Host, target.Port).ConfigureAwait(false);
        if (!result.Reachable && conn.Gateway is { Mode: "detect" } gw && gw.Host.Length > 0)
        {
            result = await ReachabilityProbe.ProbeAsync(gw.Host, 443).ConfigureAwait(false);
            if (result.Reachable) result = result with { ViaGateway = true };
        }
        lock (_gate) _status[id] = result;
        StatusChanged?.Invoke(new Dictionary<string, ProbeResult> { [id] = result });
        if (!result.Reachable && !force)
        {
            Progress(id, "check", "failed");
            var (title, message) = DisconnectReasons.ExplainProbe(result.Reason);
            store.Audit.Write("connect_blocked", new Dictionary<string, object?> { ["connectionId"] = id, ["name"] = conn.Name, ["host"] = conn.Host, ["reason"] = result.Reason });
            return new UnreachableOutcome(title, message, $"{target.Host}:{target.Port} – {result.Detail ?? result.Reason}");
        }
        Progress(id, "check", result.Reachable ? "done" : "skipped");
        if (conn.Protocol == Protocols.Ssh) return StartSsh(id, conn);

        // 2. Settings
        Progress(id, "validate", "running");
        var settings = store.Settings;
        var launch = EffectiveLaunch(quick ? WithLaunch(settings, "direct") : settings);
        Progress(id, "validate", "done");

        // 3. Credentials
        Progress(id, "secure", "running");
        var credential = new CredentialInfo(false);
        try { credential = credentials.Get(conn.Host); } catch (InvalidOperationException) { /* Credential Manager unavailable */ }
        Progress(id, "secure", "done", credential.Saved && !conn.PromptAlways ? "saved" : "prompt");

        // 4. Start
        Progress(id, "start", "running");
        SessionInfo? session = null;
        if (EmbeddedBlocker(conn, settings) is null)
        {
            try
            {
                session = await tabs.StartEmbeddedAsync(conn, settings.KeysToRemote, policy.AllowSavedCredentials != false).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Warn("Session tab could not start; using a separate window", e);
                store.Audit.Write("embedded_unavailable", new Dictionary<string, object?> { ["connectionId"] = id, ["error"] = e.Message });
            }
        }
        if (session is not null)
        {
            Touch(id, session.StartedAt);
            store.Audit.Write("connect_started", StartedDetails(session, conn, "embedded"));
            Progress(id, "start", "done");
            return new StartedOutcome(session, credential.Saved, null);
        }
        try
        {
            session = await sessions.StartMstscAsync(conn, launch.LaunchMode, launch.SigningThumbprint).ConfigureAwait(false);
        }
        catch
        {
            Progress(id, "start", "failed");
            throw;
        }
        Touch(id, session.StartedAt);
        var details = StartedDetails(session, conn, session.LaunchMode);
        details["signed"] = session.Signed;
        details["promptAlways"] = conn.PromptAlways;
        store.Audit.Write("connect_started", details);
        Progress(id, "start", "done");
        return new StartedOutcome(session, credential.Saved && !conn.PromptAlways, launch.Note);
    }

    private static AppSettings WithLaunch(AppSettings s, string mode)
    {
        var c = s.Clone();
        c.LaunchMode = mode;
        return c;
    }

    private static Dictionary<string, object?> StartedDetails(SessionInfo s, Connection conn, string launchMode) => new()
    {
        ["sessionId"] = s.Id, ["connectionId"] = conn.Id, ["name"] = conn.Name, ["host"] = conn.Host, ["gateway"] = s.Gateway,
        ["launchMode"] = launchMode,
        ["redirect"] = new Dictionary<string, object?>
        {
            ["clipboard"] = conn.Redirect!.Clipboard, ["drives"] = conn.Redirect.Drives, ["printers"] = conn.Redirect.Printers,
            ["audio"] = conn.Redirect.Audio, ["microphone"] = conn.Redirect.Microphone, ["smartcards"] = conn.Redirect.Smartcards,
        },
        ["credentialProtection"] = conn.Security!.CredentialProtection,
    };

    /// <summary>Why a connection cannot open as a tab (then it uses a separate Remote Desktop window), or null.</summary>
    public static string? EmbeddedBlocker(Connection conn, AppSettings settings)
    {
        if (settings.SessionWindow == "external") return "settings";
        if (conn.Display?.Multimon == true) return "multiple monitors"; // spanning needs mstsc
        if (conn.PromptAlways) return "different user";
        return null;
    }

    private void Touch(string id, DateTimeOffset at)
    {
        lock (_gate) if (_adhoc.ContainsKey(id)) return;
        var iso = ConnectionNormalizer.IsoOf(at);
        if (IsCentral(id)) store.PatchCentral(id, lastConnectedAt: iso);
        else store.Patch(id, c => c.LastConnectedAt = iso);
        ConnectionsChanged?.Invoke();
        RecentChanged?.Invoke();
    }

    /// <summary>Web connections open in the default browser. There is no session to track.</summary>
    private OpenedOutcome OpenWeb(string id, Connection conn)
    {
        var url = Launchers.WebTarget(conn);
        Launchers.OpenUrl(url);
        Touch(id, DateTimeOffset.UtcNow);
        store.Audit.Write("web_opened", new Dictionary<string, object?> { ["connectionId"] = id, ["name"] = conn.Name, ["url"] = url });
        return new OpenedOutcome(url);
    }

    /// <summary>SSH: the Windows OpenSSH client in its own console window. ssh asks for passwords and host keys itself.</summary>
    private ConnectOutcome StartSsh(string id, Connection conn)
    {
        Progress(id, "validate", "running");
        if (!Launchers.SshAvailable)
        {
            Progress(id, "validate", "failed");
            return new UnreachableOutcome("The SSH client is not installed",
                "Windows needs the optional feature \"OpenSSH Client\". Install it under Settings > System > Optional features, or ask your IT service desk.",
                $"{Launchers.SshExe} not found");
        }
        var arguments = Launchers.SshCmdArguments(conn, policy.SshStrictHostKeyChecking);
        Progress(id, "validate", "done");
        Progress(id, "secure", "done", "terminal");
        Progress(id, "start", "running");
        SessionInfo session;
        try { session = sessions.StartConsole(conn, Launchers.CmdExe, arguments); }
        catch { Progress(id, "start", "failed"); throw; }
        Touch(id, session.StartedAt);
        store.Audit.Write("connect_started", new Dictionary<string, object?>
        {
            ["sessionId"] = session.Id, ["connectionId"] = id, ["protocol"] = "ssh", ["name"] = conn.Name, ["host"] = conn.Host, ["port"] = conn.Port,
            ["jumpHost"] = conn.Ssh?.JumpHost is { Length: > 0 } j ? j : null, ["keyFile"] = conn.Ssh?.IdentityFile.Length > 0,
        });
        Progress(id, "start", "done");
        return new StartedOutcome(session, false, null);
    }

    // ── Sessions ─────────────────────────────────────────
    public void FocusSession(string sessionId)
    {
        if (tabs.Has(sessionId)) tabs.Focus(sessionId);
        else sessions.Focus(sessionId);
    }

    public void Disconnect(string sessionId) => sessions.Disconnect(sessionId);

    /// <summary>Audit entries for session state changes (port of onSessionUpdate).</summary>
    private readonly HashSet<string> _loggedActive = [];
    private readonly HashSet<string> _loggedEnd = [];

    public void AuditSessionUpdate(SessionInfo s)
    {
        lock (_gate)
        {
            if (s.State == SessionState.Active && _loggedActive.Add(s.Id))
                store.Audit.Write("session_connected", new Dictionary<string, object?> { ["sessionId"] = s.Id, ["connectionId"] = s.ConnectionId, ["name"] = s.Name, ["host"] = s.Host, ["verified"] = s.Verified });
            if (s.State == SessionState.Reconnecting)
                store.Audit.Write("session_interrupted", new Dictionary<string, object?> { ["sessionId"] = s.Id, ["connectionId"] = s.ConnectionId, ["host"] = s.Host, ["code"] = s.Result?.Code });
            if (s.State is SessionState.Ended or SessionState.Failed && _loggedEnd.Add(s.Id))
            {
                var duration = s.ConnectedAt is { } c && s.EndedAt is { } e ? (int)Math.Round((e - c).TotalSeconds) : 0;
                store.Audit.Write("session_ended", new Dictionary<string, object?>
                {
                    ["sessionId"] = s.Id, ["connectionId"] = s.ConnectionId, ["name"] = s.Name, ["host"] = s.Host,
                    ["state"] = s.State == SessionState.Failed ? "failed" : "ended", ["code"] = s.Result?.Code, ["title"] = s.Result?.Title, ["durationSec"] = duration,
                });
            }
        }
    }

    public List<Connection> RecentConnections(int n)
        => AllConnections().Where(c => c.LastConnectedAt is not null).OrderByDescending(c => c.LastConnectedAt, StringComparer.Ordinal).Take(n).ToList();
}
