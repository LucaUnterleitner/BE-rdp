using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Validation;

namespace RdpManager.Infrastructure.Storage;

/// <summary>
/// Local audit log (JSON Lines, same format as the Electron app). Rotates at 5 MB; the previous file is kept as
/// audit.1.log. Contains connections, results, error codes and changes, never passwords or tokens.
/// </summary>
public sealed class AuditLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly string _file;
    private readonly string _dir;
    private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private int _pendingCount;
    private TaskCompletionSource _idle = NewIdle();

    public AuditLog(string file, string dir)
    {
        _file = file;
        _dir = dir;
        _writer = Task.Run(WriteLoopAsync);
        _idle.TrySetResult();
    }

    public static string CurrentUser
    {
        get
        {
            var domain = Environment.UserDomainName;
            return string.IsNullOrEmpty(domain) ? Environment.UserName : $"{domain}\\{Environment.UserName}";
        }
    }

    /// <summary>Queues one entry. Values: string, bool, numbers, null, nested dictionaries and lists.</summary>
    public void Write(string evt, IReadOnlyDictionary<string, object?>? details = null)
    {
        var buffer = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("ts", ConnectionNormalizer.IsoNow());
            w.WriteString("user", CurrentUser);
            w.WriteString("event", evt);
            if (details is not null)
                foreach (var (k, v) in details)
                {
                    if (k is "password" or "token") continue; // defense in depth
                    w.WritePropertyName(k);
                    WriteValue(w, v);
                }
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        if (Interlocked.Increment(ref _pendingCount) == 1) Interlocked.Exchange(ref _idle, NewIdle());
        _queue.Writer.TryWrite(buffer.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case IReadOnlyDictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (var (k, x) in dict) { w.WritePropertyName(k); WriteValue(w, x); }
                w.WriteEndObject();
                break;
            case IEnumerable list:
                w.WriteStartArray();
                foreach (var x in list) WriteValue(w, x);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }

    private static TaskCompletionSource NewIdle() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FlushAsync() => Volatile.Read(ref _pendingCount) == 0 ? Task.CompletedTask : _idle.Task;

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var lines = new List<byte[]>();
            while (reader.TryRead(out var line)) lines.Add(line);
            try
            {
                Directory.CreateDirectory(_dir);
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > MaxBytes) File.Move(_file, Path.Combine(_dir, "audit.1.log"), true);
                await using var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read);
                foreach (var l in lines) await fs.WriteAsync(l).ConfigureAwait(false);
            }
            catch (IOException) { /* auditing must never block a connection */ }
            catch (UnauthorizedAccessException) { }
            if (Interlocked.Add(ref _pendingCount, -lines.Count) == 0) _idle.TrySetResult();
        }
    }

    /// <summary>The newest entries first. Reads only the tail of the file (512 KB). Call off the UI thread.</summary>
    public List<AuditEntry> ReadRecent(int limit = 300)
    {
        if (!File.Exists(_file)) return [];
        byte[] buf;
        long size;
        using (var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            size = fs.Length;
            var length = (int)Math.Min(size, 512 * 1024);
            buf = new byte[length];
            fs.Seek(size - length, SeekOrigin.Begin);
            fs.ReadExactly(buf);
        }
        var text = Encoding.UTF8.GetString(buf);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(size > buf.Length ? 1 : 0).ToList();
        var result = new List<AuditEntry>(Math.Min(limit, lines.Count));
        for (var i = lines.Count - 1; i >= 0 && result.Count < limit; i--)
        {
            try
            {
                var e = JsonSerializer.Deserialize(lines[i], RdpJsonContext.Default.AuditEntry);
                if (e is not null) result.Add(e);
            }
            catch (JsonException) { /* skip broken line */ }
        }
        return result;
    }
}
