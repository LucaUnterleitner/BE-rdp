namespace RdpManager.Core.Models;

public enum SessionState { Connecting, Active, Reconnecting, Ended, Failed }

/// <summary>Result shown to the user for an ended or failed session, or a hint while connecting.</summary>
public sealed record SessionResult(string Kind, string Title, string Message, string? Code = null, bool Retry = false, bool Hint = false)
{
    public bool IsError => Kind == "error";
}

/// <summary>Public view of a session (tab, mstsc window or SSH terminal).</summary>
public sealed class SessionInfo
{
    public required string Id { get; init; }
    public string Protocol { get; init; } = Protocols.Rdp;
    public required string ConnectionId { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string Gateway { get; init; } = "";
    public string DisplayMode { get; init; } = "";
    /// <summary>"embedded", "file", "direct" or "console".</summary>
    public string LaunchMode { get; init; } = "";
    public bool Signed { get; init; }
    public SessionState State { get; set; } = SessionState.Connecting;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? Pid { get; set; }
    public SessionResult? Result { get; set; }
    /// <summary>True when the state was confirmed by the event log or the ActiveX control.</summary>
    public bool Verified { get; set; }

    public bool IsLive => State is SessionState.Connecting or SessionState.Active or SessionState.Reconnecting;

    /// <summary>A copy for other threads (the tracker keeps changing the original).</summary>
    public SessionInfo Snapshot() => (SessionInfo)MemberwiseClone();

    public static bool Live(SessionState s) => s is SessionState.Connecting or SessionState.Active or SessionState.Reconnecting;
}

/// <summary>Result of a TCP reachability check.</summary>
public sealed record ProbeResult(bool Reachable, DateTimeOffset CheckedAt, int? LatencyMs = null, string? Reason = null, string? Detail = null, bool ViaGateway = false)
{
    public static ProbeResult Ok(int latencyMs) => new(true, DateTimeOffset.UtcNow, latencyMs);
    public static ProbeResult Fail(string reason, string? detail = null) => new(false, DateTimeOffset.UtcNow, null, reason, detail);
}

/// <summary>One line of the local audit log (JSON Lines). Never contains passwords or tokens.</summary>
public sealed class AuditEntry
{
    public string Ts { get; set; } = "";
    public string User { get; set; } = "";
    public string Event { get; set; } = "";
    public string? ConnectionId { get; set; }
    public string? SessionId { get; set; }
    public string? Name { get; set; }
    public string? Host { get; set; }
    public string? State { get; set; }
    public string? Code { get; set; }
    public string? Title { get; set; }
    public int? DurationSec { get; set; }
    public string? Reason { get; set; }
}
