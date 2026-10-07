using System.Globalization;
using RdpManager.Core.Models;

namespace RdpManager.Core.Search;

/// <summary>Filter state of the systems toolbar. The search text is not counted as an "active filter".</summary>
public sealed record FilterState(string Query = "", string Protocol = "", string Status = "", string Os = "", bool Favorites = false, bool Recent = false)
{
    public static readonly FilterState Empty = new();

    public int ActiveFilterCount => (Protocol.Length > 0 ? 1 : 0) + (Status.Length > 0 ? 1 : 0) + (Os.Length > 0 ? 1 : 0) + (Favorites ? 1 : 0) + (Recent ? 1 : 0);

    public bool IsActive => Query.Trim().Length > 0 || ActiveFilterCount > 0;
}

/// <summary>Visual status of a system: text plus icon, never color alone.</summary>
public sealed record SystemStatus(string Key, string Label, bool Dot, string Icon, string? Reason = null, int? LatencyMs = null);

/// <summary>Search, filter and status logic of the system lists (port of applyFilters/statusOf in app.js).</summary>
public static class SystemFilter
{
    public static readonly IReadOnlyDictionary<string, string> OfflineReason = new Dictionary<string, string>
    {
        ["dns"] = "Name not found", ["refused"] = "Remote Desktop not enabled", ["timeout"] = "No response", ["unreachable"] = "Network unreachable",
    };

    public static readonly IReadOnlyDictionary<string, string> RefusedByType = new Dictionary<string, string>
    {
        [Protocols.Rdp] = "Remote Desktop not enabled", [Protocols.Ssh] = "SSH server not running", [Protocols.Web] = "Web server not responding",
    };

    /// <summary>Lower-case text that a search matches against. Built once per system, not on every keystroke.</summary>
    public static string BuildSearchKey(Connection c)
        => string.Join(' ', new[] { c.Name, c.Host, Protocols.Label(Protocols.Of(c)), c.Os, c.Location, c.Folder }.Concat(c.Tags))
            .ToLowerInvariant();

    /// <summary>Search terms: every term must occur in the search key.</summary>
    public static string[] Terms(string? query)
        => (query ?? "").Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool MatchesTerms(string searchKey, string[] terms)
    {
        foreach (var t in terms)
            if (!searchKey.Contains(t, StringComparison.Ordinal)) return false;
        return true;
    }

    public static bool Matches(Connection c, string searchKey, string[] terms, FilterState f, string statusKey, DateTimeOffset now)
    {
        if (terms.Length > 0 && !MatchesTerms(searchKey, terms)) return false;
        if (f.Protocol.Length > 0 && Protocols.Of(c) != f.Protocol) return false;
        if (f.Status.Length > 0 && statusKey != f.Status) return false;
        if (f.Os.Length > 0 && c.Os != f.Os) return false;
        if (f.Favorites && !c.Favorite) return false;
        if (f.Recent && !UsedWithin(c, now, TimeSpan.FromDays(7))) return false;
        return true;
    }

    public static bool UsedWithin(Connection c, DateTimeOffset now, TimeSpan span)
        => c.LastConnectedAt is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) && t > now - span;

    /// <summary>Status of a system from its live session (if any) and the last reachability check.</summary>
    public static SystemStatus StatusOf(string protocol, SessionState? activeState, ProbeResult? probe)
    {
        if (activeState == SessionState.Connecting) return new("busy", "Connecting…", true, "");
        if (activeState == SessionState.Reconnecting) return new("busy", "Reconnecting…", false, "refresh");
        if (activeState is not null) return new("busy", protocol == Protocols.Ssh ? "Terminal open" : "Busy · your session", true, "");
        if (probe is null) return new("checking", "Checking…", false, "question");
        if (probe.Reachable) return new("available", "Available", true, "", null, probe.LatencyMs);
        if (probe.Reason == "error") return new("unknown", "Unknown", false, "question");
        return new("offline", "Offline", false, "xCircle", probe.Reason);
    }

    /// <summary>Tooltip for an offline system ("Name not found", …).</summary>
    public static string ReasonText(string protocol, string? reason)
    {
        if (reason is null) return "";
        if (reason == "refused") return RefusedByType.GetValueOrDefault(protocol, "");
        return OfflineReason.GetValueOrDefault(reason, "");
    }

    public static int CompareByName(Connection a, Connection b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
}
