using System.Net;
using RdpManager.Core.Central;
using RdpManager.Core.Models;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.Infrastructure.Central;

/// <summary>State of the central list for banners and Settings.</summary>
public sealed record CentralInfo(bool Configured, string State, int? Version = null, string? IssuedAt = null, int Count = 0, bool Stale = false, DateTimeOffset? FetchedAt = null, string? Error = null)
{
    public static readonly CentralInfo Off = new(false, "off");
}

/// <summary>
/// Downloads (HTTPS with Windows Integrated Authentication, or a file/UNC path), verifies and caches the central
/// system list (port of central.js). Older versions than the cached one are rejected (rollback protection).
/// </summary>
public sealed class CentralListService(CentralListConfig? config, string cacheDir, Func<ConnectionDefaults> defaults)
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CentralListConfig? Config => config;
    public IReadOnlyList<Connection> Systems { get; private set; } = [];
    public CentralInfo Info { get; private set; } = config is null ? CentralInfo.Off : new CentralInfo(true, "loading");

    public Connection? Get(string id) => Systems.FirstOrDefault(s => s.Id == id);

    public async Task<CentralInfo> RefreshAsync(CancellationToken ct = default)
    {
        if (config is null) return Info;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = ReadCache();
            try
            {
                var (bytes, signature) = await DownloadAsync(ct).ConfigureAwait(false);
                var doc = CentralListDocument.Verify(bytes, signature, config.PublicKey);
                if (cached is not null && doc.Version < cached.Value.Doc.Version)
                    throw new InvalidDataException($"The downloaded list (version {doc.Version}) is older than the cached one (version {cached.Value.Doc.Version}). It was not used.");
                WriteCache(bytes, signature);
                Apply(doc, "current", DateTimeOffset.UtcNow, null);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var message = e is TaskCanceledException ? "The central system list did not respond in time. Check the VPN." : e.Message;
                DiagnosticLog.Warn("Central list refresh failed", e);
                if (cached is not null) Apply(cached.Value.Doc, "offline", cached.Value.FetchedAt, message);
                else { Systems = []; Info = new CentralInfo(true, "error", Error: message); }
            }
            return Info;
        }
        finally { _gate.Release(); }
    }

    private void Apply(CentralDocument doc, string state, DateTimeOffset fetchedAt, string? error)
    {
        Systems = CentralListDocument.ToConnections(doc, defaults());
        var stale = DateTimeOffset.UtcNow - fetchedAt > TimeSpan.FromHours(config!.MaxAgeHours);
        Info = new CentralInfo(true, state, doc.Version, doc.IssuedAt, Systems.Count, stale, fetchedAt, error);
    }

    private async Task<(byte[] Bytes, string Signature)> DownloadAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(config!.Url))
        {
            var uri = new Uri(config.Url);
            // Windows Integrated Authentication (Kerberos/NTLM) only for the origin of the central list.
            var origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
            var creds = new CredentialCache
            {
                { origin, "Negotiate", CredentialCache.DefaultNetworkCredentials },
                { origin, "NTLM", CredentialCache.DefaultNetworkCredentials },
            };
            using var handler = new HttpClientHandler { Credentials = creds, PreAuthenticate = false, UseCookies = false };
            using var http = new HttpClient(handler) { Timeout = FetchTimeout, MaxResponseContentBufferSize = CentralListDocument.MaxBytes };
            http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true, NoCache = true };
            async Task<byte[]> Get(string url)
            {
                using var res = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) throw new HttpRequestException($"The central system list could not be loaded (HTTP {(int)res.StatusCode}).");
                return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            var bytesTask = Get(config.Url);
            var sigTask = Get(config.Url + ".sig");
            await Task.WhenAll(bytesTask, sigTask).ConfigureAwait(false);
            return (bytesTask.Result, System.Text.Encoding.UTF8.GetString(sigTask.Result));
        }
        var file = config.Path!;
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("The central system list was not found.");
        if (info.Length > CentralListDocument.MaxBytes) throw new InvalidDataException("The central system list is too large.");
        var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        var sig = await File.ReadAllTextAsync(file + ".sig", ct).ConfigureAwait(false);
        return (bytes, sig);
    }

    private (CentralDocument Doc, DateTimeOffset FetchedAt)? ReadCache()
    {
        try
        {
            var file = Path.Combine(cacheDir, "systems.json");
            var bytes = File.ReadAllBytes(file);
            var sig = File.ReadAllText(file + ".sig");
            var doc = CentralListDocument.Verify(bytes, sig, config!.PublicKey);
            return (doc, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private void WriteCache(byte[] bytes, string signature)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "systems.json"), bytes);
            File.WriteAllText(Path.Combine(cacheDir, "systems.json.sig"), signature);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the cache is optional */ }
    }
}
