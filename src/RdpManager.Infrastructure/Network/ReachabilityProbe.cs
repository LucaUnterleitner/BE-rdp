using System.Diagnostics;
using System.Net.Sockets;
using RdpManager.Core.Models;

namespace RdpManager.Infrastructure.Network;

public sealed record ProbeTarget(string Id, string Host, int Port, bool ViaGateway = false);

/// <summary>TCP reachability check (ICMP is often blocked, so the RDP or gateway port is opened and closed again).</summary>
public static class ReachabilityProbe
{
    public static async Task<ProbeResult> ProbeAsync(string host, int port, int timeoutMs = 3000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return ProbeResult.Ok((int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeResult.Fail("timeout");
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ProbeResult.Fail("dns", e.SocketErrorCode.ToString()),
                SocketError.ConnectionRefused => ProbeResult.Fail("refused", e.SocketErrorCode.ToString()),
                SocketError.TimedOut => ProbeResult.Fail("timeout", e.SocketErrorCode.ToString()),
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => ProbeResult.Fail("unreachable", e.SocketErrorCode.ToString()),
                _ => ProbeResult.Fail("error", e.SocketErrorCode.ToString()),
            };
        }
        catch (ArgumentException e)
        {
            return ProbeResult.Fail("error", e.Message);
        }
    }

    /// <summary>Runs probes with limited concurrency so servers are not hammered.</summary>
    public static async Task<Dictionary<string, ProbeResult>> ProbeManyAsync(IReadOnlyList<ProbeTarget> targets, int concurrency = 6, CancellationToken ct = default)
    {
        var results = new Dictionary<string, ProbeResult>();
        var gate = new object();
        var index = -1;
        async Task Worker()
        {
            while (true)
            {
                var i = Interlocked.Increment(ref index);
                if (i >= targets.Count || ct.IsCancellationRequested) return;
                var t = targets[i];
                var r = await ProbeAsync(t.Host, t.Port, 3000, ct).ConfigureAwait(false);
                if (t.ViaGateway) r = r with { ViaGateway = true };
                lock (gate) results[t.Id] = r;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, targets.Count)).Select(_ => Task.Run(Worker, ct))).ConfigureAwait(false);
        return results;
    }
}
