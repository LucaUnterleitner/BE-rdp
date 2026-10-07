using System.Diagnostics;
using System.Globalization;
using System.Text;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.App.Services;

/// <summary>
/// Startup timings, measured from the process start time reported by Windows (includes runtime startup).
/// Written to the app log; PerfRunner adds them to the performance report.
/// </summary>
public static class StartupMetrics
{
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private static readonly Dictionary<string, double> Marks = [];

    public static IReadOnlyDictionary<string, double> All { get { lock (Marks) return new Dictionary<string, double>(Marks); } }

    public static void Mark(string name)
    {
        var ms = (DateTime.UtcNow - ProcessStart).TotalMilliseconds;
        lock (Marks) Marks.TryAdd(name, Math.Round(ms, 1));
    }

    public static void WriteLog()
    {
        var sb = new StringBuilder("Startup (ms since process start):");
        foreach (var (k, v) in All) sb.Append(' ').Append(k).Append('=').Append(v.ToString(CultureInfo.InvariantCulture));
        DiagnosticLog.Info(sb.ToString());
    }
}
