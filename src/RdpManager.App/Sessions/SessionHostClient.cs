using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.App.Sessions;

/// <summary>
/// One session host process per tab (this exe with --session-host). Commands go to its stdin, events come back as
/// one JSON object per line on its stdout. Only this process holds the pipes, so no other program can control a
/// session. Events are raised on a background thread.
/// </summary>
public sealed class SessionHostClient : IDisposable
{
    private static readonly HashSet<string> Events =
    [
        "ready", "window", "fatal", "connecting", "connected", "loginComplete", "disconnected", "requestFullscreen", "fatalError",
        "warning", "requestMinimize", "logonError", "focusReleased", "autoReconnected", "autoReconnecting", "error", "debug",
    ];

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();
    private volatile bool _exited;

    /// <summary>(type, data) for every event of the session host.</summary>
    public event Action<string, JsonElement>? Message;
    public event Action? Exited;

    public int Pid { get; }
    public bool HasExited => _exited;
    /// <summary>Top-level window of the session host (reported after start), used to park the session safely.</summary>
    public IntPtr WindowHandle { get; private set; }
    /// <summary>Last attach/resize parameters, so unchanged layouts are not sent again.</summary>
    internal string? LastAttach { get; set; }
    internal string? LastSize { get; set; }

    public SessionHostClient()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("--session-host");
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _process = Process.Start(psi) ?? throw new InvalidOperationException("The Remote Desktop component could not be started.");
        Pid = _process.Id;
        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => OnExit();
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = $"session-host-{Pid}" };
        reader.Start();
    }

    private void ReadLoop()
    {
        try
        {
            string? line;
            while ((line = _process.StandardOutput.ReadLine()) is not null)
            {
                if (line.Length == 0 || line.Length > 1024 * 1024) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;
                    var type = t.GetString()!;
                    if (!Events.Contains(type)) continue;
                    var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
                    if (type == "ready") _ready.TrySetResult();
                    if (type == "window" && data.ValueKind == JsonValueKind.Number && data.TryGetInt64(out var hwnd)) WindowHandle = new IntPtr(hwnd);
                    if (type == "fatal") _ready.TrySetException(new InvalidOperationException(data.ValueKind == JsonValueKind.String ? data.GetString() : "The Remote Desktop component is not available."));
                    if (type is "debug" or "error") DiagnosticLog.Info($"session host {Pid}: {type} {(data.ValueKind == JsonValueKind.String ? data.GetString() : data.ToString())}");
                    Message?.Invoke(type, data);
                }
                catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { /* ignore a malformed line */ }
            }
        }
        catch (IOException) { }
        OnExit();
    }

    private void OnExit()
    {
        if (_exited) return;
        _exited = true;
        _ready.TrySetException(new InvalidOperationException("The Remote Desktop component stopped unexpectedly."));
        Exited?.Invoke();
    }

    public async Task WhenReadyAsync(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_ready.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (done != _ready.Task) throw new TimeoutException("The Remote Desktop component did not start in time.");
        await _ready.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a command. args is written as JSON with System.Text.Json (no string concatenation).</summary>
    public bool Send(string cmd, object? args = null)
    {
        if (_exited) return false;
        var buffer = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("cmd", cmd);
            w.WritePropertyName("args");
            if (args is null) { w.WriteStartObject(); w.WriteEndObject(); }
            else JsonSerializer.Serialize(w, args, args.GetType());
            w.WriteEndObject();
        }
        var line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        lock (_writeLock)
        {
            try { _stdin.WriteLine(line); return true; }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    /// <summary>Ends the session; the host exits after the control reports the disconnect. Killed if it does not.</summary>
    public void Disconnect()
    {
        Send("disconnect");
        _ = Task.Delay(5000).ContinueWith(_ => { if (!_exited) Kill(); }, TaskScheduler.Default);
    }

    public void Kill()
    {
        try { _process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        try { _stdin.Dispose(); } catch (IOException) { }
        _process.Dispose();
    }
}
