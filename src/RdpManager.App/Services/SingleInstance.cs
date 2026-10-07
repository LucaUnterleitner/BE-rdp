using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.App.Services;

/// <summary>
/// One instance per user session. A second start sends its arguments over a named pipe that only the same
/// user can open, then exits. Only "--connect=&lt;id&gt;" and "--show" are acted on.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string UserSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
    // Per user and per Windows session (RDS/Citrix users can be signed in twice), like the Local\ mutex.
    private static readonly string Name = $"BearingPoint.RemoteDesktop.{UserSid}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) { _mutex = mutex; }

    /// <summary>Raised on a background thread with the arguments of a second start.</summary>
    public event Action<string[]>? ArgumentsReceived;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, $"Local\\{Name}", out var created);
        if (created) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void Forward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            var text = string.Join('\n', args.Length == 0 ? ["--show"] : args);
            var bytes = Encoding.UTF8.GetBytes(text.Length > 4096 ? text[..4096] : text);
            pipe.Write(bytes);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { /* first instance is busy or closing */ }
    }

    public void Listen()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // CurrentUserOnly: only processes of the same user can connect (the pipe gets a per-user ACL).
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var ms = new MemoryStream();
                    var buffer = new byte[4096];
                    int n;
                    while ((n = await server.ReadAsync(buffer, _cts.Token)) > 0 && ms.Length < 8192) ms.Write(buffer, 0, n);
                    var args = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    ArgumentsReceived?.Invoke(args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    DiagnosticLog.Warn("Single-instance pipe failed", e);
                    await Task.Delay(1000);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
