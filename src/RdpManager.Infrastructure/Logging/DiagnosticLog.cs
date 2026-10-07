using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace RdpManager.Infrastructure.Logging;

/// <summary>
/// Technical log for support (%LOCALAPPDATA%\BearingPoint\RemoteDesktop\logs\app.log, rotated at 1 MB).
/// Writes happen on a background task, never on the UI thread. Never log passwords, tokens or ID token claims:
/// callers pass only technical information, and <see cref="Redact"/> strips anything that looks like a secret.
/// </summary>
public static class DiagnosticLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static string? _file;
    private static Task? _writer;

    public static void Initialize(string logsDir)
    {
        if (_writer is not null) return;
        _file = Path.Combine(logsDir, "app.log");
        _writer = Task.Run(WriteLoopAsync);
    }

    public static void Info(string message) => Enqueue("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Enqueue("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Enqueue("ERROR", message, ex);

    /// <summary>Waits until queued lines are written (on exit).</summary>
    public static void Flush(TimeSpan timeout)
    {
        Queue.Writer.TryComplete();
        try { _writer?.Wait(timeout); } catch (AggregateException) { }
    }

    private static void Enqueue(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder(128)
            .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(level).Append(' ').Append(Redact(message));
        if (ex is not null) sb.Append(" | ").Append(level == "ERROR" ? Redact(ex.ToString()) : ex.GetType().Name + ": " + Redact(ex.Message));
        Queue.Writer.TryWrite(sb.ToString());
    }

    /// <summary>Removes values of fields that commonly hold secrets (password=, token=, Bearer …).</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(password|passwd|pwd|secret|token|assertion)(\s*[=:]\s*)\S+", "$1$2***");
        return System.Text.RegularExpressions.Regex.Replace(t, @"(?i)bearer\s+[A-Za-z0-9\-_.~+/=]+", "Bearer ***");
    }

    private static async Task WriteLoopAsync()
    {
        var reader = Queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new StringBuilder();
            while (reader.TryRead(out var line)) batch.Append(line).Append(Environment.NewLine);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file!)!);
                var info = new FileInfo(_file!);
                if (info.Exists && info.Length > MaxBytes) File.Move(_file!, Path.ChangeExtension(_file!, ".1.log"), true);
                await File.AppendAllTextAsync(_file!, batch.ToString()).ConfigureAwait(false);
            }
            catch (IOException) { /* logging must never break the app */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
