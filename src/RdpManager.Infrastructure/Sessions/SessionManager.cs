using System.Collections.Concurrent;
using System.Diagnostics;
using RdpManager.Core.Models;
using RdpManager.Core.Rdp;
using RdpManager.Core.Sessions;
using RdpManager.Core.Validation;
using RdpManager.Infrastructure.Logging;
using RdpManager.Infrastructure.Rdp;
using RdpManager.Infrastructure.Windows;

namespace RdpManager.Infrastructure.Sessions;

/// <summary>
/// Starts and tracks sessions (port of sessions.js): separate mstsc windows (state from the process lifetime and
/// the RDP client event log, matched by process id), SSH terminals, and sessions inside the app (registered by
/// the UI layer, state from the ActiveX control). Raises <see cref="Updated"/> on a background thread.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    public static readonly string Mstsc = Path.Combine(System32, "mstsc.exe");
    private static readonly string RdpSign = Path.Combine(System32, "rdpsign.exe");
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BackgroundPoll = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TmpFileMaxAge = TimeSpan.FromSeconds(30);

    private sealed class Entry(TrackedSession tracked, string kind)
    {
        public TrackedSession Tracked { get; } = tracked;
        public string Kind { get; } = kind; // mstsc | console | embedded
        public Process? Process { get; set; }
        public Action? Kill { get; set; }
        public Action? Focus { get; set; }
        private int _running = 1;
        /// <summary>Serializes state changes of this session (poll thread and exit thread).</summary>
        public object Gate { get; } = new();
        public bool Running { get => Volatile.Read(ref _running) == 1; set => Volatile.Write(ref _running, value ? 1 : 0); }
        /// <summary>Atomically marks the client as stopped; true only for the first caller.</summary>
        public bool TryStop() => Interlocked.Exchange(ref _running, 0) == 1;
    }

    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly string _tmpDir;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private Timer? _pollTimer;
    private volatile bool _background;

    public SessionManager(string tmpDir)
    {
        _tmpDir = tmpDir;
        Task.Run(SweepTmp);
    }

    /// <summary>Raised on every state change with a snapshot of the session.</summary>
    public event Action<SessionInfo>? Updated;

    public IReadOnlyList<SessionInfo> List()
        => _sessions.Values.Select(e => e.Tracked.Info.Snapshot()).OrderByDescending(s => s.StartedAt).ToList();

    public SessionInfo? Get(string id) => _sessions.TryGetValue(id, out var e) ? e.Tracked.Info.Snapshot() : null;

    public SessionInfo? ActiveFor(string connectionId)
        => _sessions.Values.Select(e => e.Tracked.Info).Where(s => s.ConnectionId == connectionId && s.IsLive).OrderByDescending(s => s.StartedAt).FirstOrDefault()?.Snapshot();

    public int LiveCount => _sessions.Values.Count(e => e.Tracked.Info.IsLive);

    public bool IsEmbedded(string id) => _sessions.TryGetValue(id, out var e) && e.Kind == "embedded";

    private void Raise(SessionInfo s)
    {
        try { Updated?.Invoke(s.Snapshot()); }
        catch (Exception e) { DiagnosticLog.Error("Session update handler failed", e); }
    }

    /// <summary>Poll active sessions less often while nobody looks at the window. Connecting sessions stay fast.</summary>
    public void SetBackground(bool on)
    {
        var was = _background;
        _background = on;
        if (was && !on) SchedulePoll(TimeSpan.Zero);
    }

    // ── Separate Remote Desktop windows (mstsc) ──────────
    /// <summary>
    /// Launches mstsc. "file" writes a temporary .rdp file (all settings apply; Windows shows its security
    /// confirmation unless the file is signed by a trusted publisher). "direct" uses mstsc /v: switches only.
    /// </summary>
    public async Task<SessionInfo> StartMstscAsync(Connection conn, string launchMode, string? signingThumbprint)
    {
        var id = Guid.NewGuid().ToString();
        List<string> args;
        string? rdpPath = null;
        if (launchMode == "direct")
        {
            args = RdpFile.BuildDirectArgs(conn);
        }
        else
        {
            Directory.CreateDirectory(_tmpDir);
            rdpPath = Path.Combine(_tmpDir, $"{id}.rdp");
            await File.WriteAllBytesAsync(rdpPath, RdpFile.Encode(RdpFile.Build(conn))).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(signingThumbprint))
            {
                try { await SignFileAsync(rdpPath, signingThumbprint).ConfigureAwait(false); }
                catch { Cleanup(rdpPath); throw; }
            }
            args = [rdpPath, .. RdpFile.ProtectionArgs(conn)];
        }

        var info = new SessionInfo
        {
            Id = id,
            Protocol = Protocols.Rdp,
            ConnectionId = conn.Id,
            Name = conn.Name,
            Host = conn.Host,
            Port = conn.Port == 0 ? 3389 : conn.Port,
            Gateway = conn.Gateway is { Mode: not "none" } g ? g.Host : "",
            DisplayMode = MstscSessionStateMachine.DescribeDisplay(conn.Display),
            LaunchMode = launchMode,
            Signed = rdpPath is not null && !string.IsNullOrEmpty(signingThumbprint),
        };
        var tracked = new TrackedSession(info) { RdpPath = rdpPath };
        var entry = new Entry(tracked, "mstsc");

        // ArgumentList quotes each argument by the CommandLineToArgvW rules; no shell is involved.
        var psi = new ProcessStartInfo(Mstsc) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("mstsc.exe did not start.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Cleanup(rdpPath);
            throw new InvalidOperationException($"The Remote Desktop client could not be started: {e.Message}", e);
        }
        info.Pid = process.Id;
        entry.Process = process;
        entry.Kill = () => { try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } };
        entry.Focus = () => NativeWindows.FocusProcessWindow(process.Id);
        _sessions[id] = entry;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = OnMstscExitAsync(entry);
        if (process.HasExited) _ = OnMstscExitAsync(entry);
        Raise(info);
        SchedulePoll(FastPoll);
        return info;
    }

    private static async Task SignFileAsync(string file, string thumbprint)
    {
        var psi = new ProcessStartInfo(RdpSign) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/sha256");
        psi.ArgumentList.Add(thumbprint);
        psi.ArgumentList.Add(file);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("rdpsign.exe did not start.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var text = ((await stderr.ConfigureAwait(false)) + (await stdout.ConfigureAwait(false))).Trim();
            throw new InvalidOperationException($"The connection file could not be signed (rdpsign): {text}");
        }
    }

    private async Task OnMstscExitAsync(Entry entry)
    {
        // Exited can fire twice (event and HasExited check); only the first call finishes the session.
        if (!entry.TryStop()) return;
        var t = entry.Tracked;
        Cleanup(t.RdpPath);
        t.RdpPath = null;
        var events = t.FailedEarly ? null : await Task.Run(() => RdpClientEventLog.Query(t.Info.StartedAt, [t.Info.Pid ?? 0])).ConfigureAwait(false);
        SessionInfo snapshot;
        lock (entry.Gate)
        {
            MstscSessionStateMachine.OnExit(t, events, DateTimeOffset.UtcNow);
            snapshot = t.Info.Snapshot();
        }
        entry.Process?.Dispose();
        Raise(snapshot);
    }

    private void SchedulePoll(TimeSpan due)
    {
        _pollTimer ??= new Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Shared event log poller for all live mstsc sessions.</summary>
    private async Task PollAsync()
    {
        if (!await _pollGate.WaitAsync(0).ConfigureAwait(false)) { SchedulePoll(FastPoll); return; }
        try
        {
            var live = _sessions.Values.Where(e => e.Kind == "mstsc" && e.Running && e.Tracked.Info.IsLive).ToList();
            if (live.Count == 0) return;
            var since = live.Min(e => e.Tracked.Info.StartedAt);
            var events = RdpClientEventLog.Query(since, live.Select(e => e.Tracked.Info.Pid ?? 0).ToList());
            var now = DateTimeOffset.UtcNow;
            foreach (var e in live)
            {
                try
                {
                    SessionAction action;
                    SessionInfo snapshot;
                    lock (e.Gate)
                    {
                        // The process may have exited while the event log was read: its final state wins.
                        if (!e.Running || !e.Tracked.Info.IsLive) continue;
                        action = MstscSessionStateMachine.Apply(e.Tracked, events, now);
                        snapshot = e.Tracked.Info.Snapshot();
                    }
                    if (action.HasFlag(SessionAction.RemoveConnectionFile)) { Cleanup(e.Tracked.RdpPath); e.Tracked.RdpPath = null; }
                    if (action.HasFlag(SessionAction.KillClient)) e.Kill?.Invoke();
                    if (action.HasFlag(SessionAction.Changed)) Raise(snapshot);
                }
                catch (Exception ex) { DiagnosticLog.Warn("Session state update failed", ex); }
            }
        }
        finally
        {
            _pollGate.Release();
            var mstsc = _sessions.Values.Where(e => e.Kind == "mstsc" && e.Running && e.Tracked.Info.IsLive).ToList();
            if (mstsc.Count > 0)
            {
                var fast = mstsc.Any(e => e.Tracked.Info.State is SessionState.Connecting or SessionState.Reconnecting);
                SchedulePoll(fast ? FastPoll : _background ? BackgroundPoll : SlowPoll);
            }
        }
    }

    // ── SSH terminal ─────────────────────────────────────
    /// <summary>
    /// Starts a console program (ssh in cmd.exe) in its own console window. The session is "active" while the
    /// program runs; exit code 255 means ssh itself failed (network, host key, sign-in).
    /// </summary>
    public SessionInfo StartConsole(Connection conn, string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = false };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("The program could not be started.");
        var now = DateTimeOffset.UtcNow;
        var info = new SessionInfo
        {
            Id = Guid.NewGuid().ToString(),
            Protocol = conn.Protocol,
            ConnectionId = conn.Id,
            Name = conn.Name,
            Host = conn.Host,
            Port = conn.Port,
            Username = conn.Username,
            Gateway = conn.Ssh?.JumpHost ?? "",
            DisplayMode = "Terminal window",
            LaunchMode = "console",
            State = SessionState.Active,
            StartedAt = now,
            ConnectedAt = now,
            Pid = process.Id,
        };
        var entry = new Entry(new TrackedSession(info), "console")
        {
            Process = process,
            Kill = () => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } },
            Focus = () => NativeWindows.FocusConsoleOf(process.Id),
        };
        _sessions[info.Id] = entry;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            entry.Running = false;
            var code = SafeExitCode(process);
            info.EndedAt = DateTimeOffset.UtcNow;
            if (entry.Tracked.UserDisconnect)
            {
                info.State = SessionState.Ended;
                info.Result = new SessionResult("normal", "Terminal closed", "You closed the SSH session from the app.");
            }
            else if (code == 255)
            {
                info.State = SessionState.Failed;
                info.Result = new SessionResult("error", "SSH connection failed", "ssh could not connect or sign in. The terminal window shows the reason, for example an unknown host, a changed host key or a wrong password.", "255");
            }
            else
            {
                info.State = SessionState.Ended;
                info.Result = new SessionResult("normal", "Session closed", "The SSH session ended.");
            }
            process.Dispose();
            Raise(info);
        };
        Raise(info);
        return info;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return -1; }
    }

    // ── Sessions inside the app (tabs) ───────────────────
    /// <summary>Registers a session hosted in the app. The UI layer reports state changes through <see cref="Update"/>.</summary>
    public void RegisterEmbedded(SessionInfo info, Action disconnect, Action? focus)
    {
        var entry = new Entry(new TrackedSession(info), "embedded") { Kill = disconnect, Focus = focus };
        _sessions[info.Id] = entry;
        Raise(info);
    }

    public void Update(string id, Action<SessionInfo> change)
    {
        if (!_sessions.TryGetValue(id, out var e)) return;
        change(e.Tracked.Info);
        Raise(e.Tracked.Info);
    }

    public bool WasUserDisconnect(string id) => _sessions.TryGetValue(id, out var e) && e.Tracked.UserDisconnect;

    public void MarkStopped(string id)
    {
        if (_sessions.TryGetValue(id, out var e)) e.Running = false;
    }

    // ── Common ───────────────────────────────────────────
    public void Focus(string id)
    {
        if (!_sessions.TryGetValue(id, out var e) || !e.Running || e.Focus is null) throw new InvalidOperationException("This session is no longer open.");
        e.Focus();
    }

    /// <summary>Closes the local client. The server keeps the user's session running (disconnected).</summary>
    public bool Disconnect(string id)
    {
        if (!_sessions.TryGetValue(id, out var e) || !e.Running) return false;
        e.Tracked.UserDisconnect = true;
        e.Kill?.Invoke();
        return true;
    }

    public void DisconnectAll()
    {
        foreach (var id in _sessions.Keys.ToList()) Disconnect(id);
    }

    public void ClearEnded()
    {
        foreach (var (id, e) in _sessions)
            if (!e.Tracked.Info.IsLive && !e.Running) _sessions.TryRemove(id, out _);
    }

    /// <summary>Removes connection files left behind by a crash or by quitting while sessions were open.</summary>
    private void SweepTmp()
    {
        try
        {
            if (!Directory.Exists(_tmpDir)) return;
            foreach (var f in Directory.EnumerateFiles(_tmpDir, "*.rdp"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TmpFileMaxAge) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Cleanup(string? file)
    {
        if (file is null) return;
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _pollGate.Dispose();
    }

    /// <summary>For audit: whether a session id has a live client process.</summary>
    public bool IsRunning(string id) => _sessions.TryGetValue(id, out var e) && e.Running;

    internal static string NewId() => Guid.NewGuid().ToString();

    /// <summary>ISO timestamp helper for audit entries.</summary>
    public static string Iso(DateTimeOffset? t) => t is null ? "" : ConnectionNormalizer.IsoOf(t.Value);
}
