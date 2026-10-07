using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using RdpManager.Core.Sessions;

namespace RdpManager.Infrastructure.Rdp;

/// <summary>
/// Reads events 1024/1026/1027 of Microsoft-Windows-TerminalServices-RDPClient/Operational in-process
/// (no PowerShell, which endpoint protection products flag and which costs CPU).
/// </summary>
public static class RdpClientEventLog
{
    public const string LogName = "Microsoft-Windows-TerminalServices-RDPClient/Operational";

    /// <summary>Events of the given mstsc processes since a point in time, oldest first. Null when the log cannot be read.</summary>
    public static IReadOnlyList<RdpClientEvent>? Query(DateTimeOffset since, IReadOnlyCollection<int> pids)
    {
        var ids = pids.Where(p => p > 0).Distinct().ToList();
        if (ids.Count == 0) return [];
        var start = since.UtcDateTime.AddSeconds(-2).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var xpath = $"*[System[(EventID=1024 or EventID=1026 or EventID=1027) and TimeCreated[@SystemTime>='{start}'] and ("
            + string.Join(" or ", ids.Select(p => $"Execution[@ProcessID={p.ToString(CultureInfo.InvariantCulture)}]")) + ")]]";
        try
        {
            var events = new List<RdpClientEvent>();
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, xpath));
            for (var rec = reader.ReadEvent(); rec is not null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    var props = rec.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "").ToList();
                    var time = rec.TimeCreated is { } t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UtcNow;
                    events.Add(new RdpClientEvent(rec.Id, rec.ProcessId ?? 0, time, props));
                }
            }
            return events;
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
