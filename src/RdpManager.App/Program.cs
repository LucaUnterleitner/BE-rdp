using System.Diagnostics;
using RdpManager.App.Services;

namespace RdpManager.App;

/// <summary>
/// Entry point. The same executable runs in two modes:
/// "--session-host" hosts one RDP session (WinForms, no WPF is loaded), everything else is the app.
/// </summary>
public static class Program
{
    /// <summary>Process start, for the startup measurements (PERFORMANCE.md).</summary>
    public static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--session-host") return SessionHost.SessionHostProgram.Run(args);
        StartupMetrics.Mark("main");

        // Only one app instance per user session. A second start forwards "--connect=<id>" and exits.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.Forward(args);
            return 0;
        }
        var app = new App(args, instance);
        app.InitializeComponent();
        StartupMetrics.Mark("resources-loaded");
        return app.Run();
    }
}
