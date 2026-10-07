using RdpManager.Core.Models;
using RdpManager.Core.Rdp;

namespace RdpManager.Core.Sessions;

/// <summary>One event of Microsoft-Windows-TerminalServices-RDPClient/Operational (1024, 1026, 1027).</summary>
public sealed record RdpClientEvent(int Id, int Pid, DateTimeOffset Time, IReadOnlyList<string> Props);

/// <summary>What the caller must do after the state machine looked at the events.</summary>
[Flags]
public enum SessionAction { None = 0, Changed = 1, KillClient = 2, RemoveConnectionFile = 4 }

/// <summary>Internal tracking data of an mstsc session.</summary>
public sealed class TrackedSession(SessionInfo info)
{
    public SessionInfo Info { get; } = info;
    public DateTimeOffset LastChange { get; set; } = DateTimeOffset.UtcNow;
    public bool FailedEarly { get; set; }
    public bool UserDisconnect { get; set; }
    public string? RdpPath { get; set; }
}

/// <summary>
/// State machine for one live mstsc session, driven by its own client events (port of SessionManager.applyEvents).
/// 1024 connecting, 1027 connected, 1026 disconnected (reason code in the properties).
/// </summary>
public static class MstscSessionStateMachine
{
    public static readonly TimeSpan NoLogGrace = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan ReconnectGiveUp = TimeSpan.FromSeconds(90);

    /// <summary>Event 1026 properties look like ["Disconnect Reason", "260", "Info"].</summary>
    public static int? ReasonOf(RdpClientEvent e)
    {
        foreach (var p in e.Props)
            if (p.Length > 0 && p.All(char.IsAsciiDigit) && int.TryParse(p, out var v)) return v;
        return null;
    }

    /// <param name="events">Events of all tracked processes, oldest first; null when the event log cannot be read.</param>
    public static SessionAction Apply(TrackedSession t, IReadOnlyList<RdpClientEvent>? events, DateTimeOffset now)
    {
        var s = t.Info;
        if (events is null)
        {
            // Event log unavailable: fall back to the process lifetime after a grace period (marked unverified).
            if (s.State == SessionState.Connecting && now - s.StartedAt > NoLogGrace)
            {
                s.State = SessionState.Active;
                s.ConnectedAt = now;
                s.Verified = false;
                t.LastChange = now;
                return SessionAction.Changed;
            }
            return SessionAction.None;
        }
        var last = events.LastOrDefault(e => e.Pid == s.Pid && (e.Id == 1026 || e.Id == 1027));
        if (last is null) return SessionAction.None;

        if (last.Id == 1027)
        {
            if (s.State != SessionState.Active || !s.Verified)
            {
                s.State = SessionState.Active;
                s.ConnectedAt ??= last.Time;
                s.Verified = true;
                s.Result = null;
                t.LastChange = now;
                // mstsc has read the connection file; remove it early.
                return SessionAction.Changed | SessionAction.RemoveConnectionFile;
            }
            return SessionAction.None;
        }

        // last.Id == 1026: disconnected while the mstsc process is still alive.
        var code = ReasonOf(last);
        var result = DisconnectReasons.Explain(code);
        if (result is null || !result.IsError) return SessionAction.None; // normal close or sign-out; the process exit follows
        if (s.State == SessionState.Connecting)
        {
            if (code is not null && DisconnectReasons.FatalCodes.Contains(code.Value))
            {
                // Nothing to retry in mstsc (name, network or TLS problem): close its error box; the app explains the error.
                t.FailedEarly = true;
                s.State = SessionState.Failed;
                s.EndedAt = last.Time;
                s.Result = result;
                t.LastChange = now;
                return SessionAction.Changed | SessionAction.KillClient;
            }
            if (s.Result is null || s.Result.Code != result.Code)
            {
                // For example a wrong password: mstsc lets the user try again, so keep the window and show a hint.
                s.Result = result with { Hint = true };
                t.LastChange = now;
                return SessionAction.Changed;
            }
            return SessionAction.None;
        }
        if (s.State == SessionState.Active)
        {
            // Network drop: mstsc retries automatically (autoreconnection enabled) or shows an error box.
            s.State = SessionState.Reconnecting;
            s.Result = result;
            t.LastChange = now;
            return SessionAction.Changed;
        }
        if (s.State == SessionState.Reconnecting && now - t.LastChange > ReconnectGiveUp)
        {
            s.State = SessionState.Failed;
            s.EndedAt = last.Time;
            s.Result = result;
            t.LastChange = now;
            return SessionAction.Changed;
        }
        return SessionAction.None;
    }

    /// <summary>Final state when the mstsc process has exited (port of SessionManager.onExit).</summary>
    public static void OnExit(TrackedSession t, IReadOnlyList<RdpClientEvent>? events, DateTimeOffset now)
    {
        var s = t.Info;
        if (t.FailedEarly || s.State == SessionState.Failed)
        {
            s.EndedAt ??= now;
            return;
        }
        var wasConnected = s.State is SessionState.Active or SessionState.Reconnecting;
        int? reasonCode = null;
        if (events is not null)
        {
            // Only a disconnect that was not followed by a successful (re)connection explains the exit.
            var last = events.LastOrDefault(e => e.Pid == s.Pid && (e.Id == 1026 || e.Id == 1027));
            if (last is { Id: 1026 }) reasonCode = ReasonOf(last);
        }
        var result = DisconnectReasons.Explain(reasonCode);
        var state = SessionState.Ended;
        if (t.UserDisconnect && !wasConnected)
            result = new SessionResult("normal", "Connection cancelled", "You cancelled the connection before the session was established.");
        else if (t.UserDisconnect)
            result = new SessionResult("normal", "Disconnected", "The window was closed by you. Your remote session keeps running on the server until you sign out inside it.");
        else if (result is { IsError: true })
            state = SessionState.Failed;
        else if (!wasConnected && result is null)
            result = new SessionResult("normal", "Connection cancelled", "The Remote Desktop window was closed before the session was established.");
        else
            result ??= new SessionResult("normal", "Session closed", "The Remote Desktop window was closed. If you did not sign out, your session keeps running on the server.");
        s.State = state;
        s.EndedAt = now;
        s.Result = result;
    }

    public static string DescribeDisplay(DisplayOptions? d)
    {
        if (d is null) return "Full screen";
        if (d.Multimon) return "All monitors";
        if (d.Mode == "window") return $"Window {d.Width}×{d.Height}";
        return "Full screen";
    }
}
