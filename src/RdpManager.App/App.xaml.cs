using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RdpManager.App.Services;
using RdpManager.App.Sessions;
using RdpManager.App.ViewModels;
using RdpManager.App.Views;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Infrastructure.Auth;
using RdpManager.Infrastructure.Central;
using RdpManager.Infrastructure.Credentials;
using RdpManager.Infrastructure.Logging;
using RdpManager.Infrastructure.Migration;
using RdpManager.Infrastructure.Policy;
using RdpManager.Infrastructure.Security;
using RdpManager.Infrastructure.Sessions;
using RdpManager.Infrastructure.Storage;
using RdpManager.Infrastructure.Windows;

namespace RdpManager.App;

/// <summary>
/// Startup: the main window is created and shown first (with a loading state). Migration, policy, data and the
/// group check run on a background thread; services are composed afterwards. Tray, jump list, reachability
/// checks, the central list, Entra ID and the update check start only after the first frame was rendered.
/// </summary>
public partial class App : Application
{
    private readonly string[] _args;
    private readonly SingleInstance _instance;
    private ServiceProvider? _services;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _durationTimer;
    private TrayService? _tray;
    private bool _startHidden;
    private string? _pendingConnect;
    private string? _perfReport;

    public App(string[] args, SingleInstance instance)
    {
        _args = args;
        _instance = instance;
    }

    public static IServiceProvider? Services => (Current as App)?._services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _startHidden = _args.Contains("--hidden") || PackageInfo.IsStartupTaskLaunch();
        _pendingConnect = ConnectArg(_args);
        // Development and measurement switches; ignored in the installed (packaged) app.
        string? dataDir = null;
        if (!PackageInfo.IsPackaged)
        {
            dataDir = ArgValue("--data-dir");
            _perfReport = ArgValue("--perf-report");
        }
        var paths = AppPaths.Create(dataDir);
        DiagnosticLog.Initialize(paths.LogsDir);
        DiagnosticLog.Info($"Start {typeof(App).Assembly.GetName().Version} packaged={PackageInfo.IsPackaged}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, ev) => { DiagnosticLog.Error("Unobserved task exception", ev.Exception); ev.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => DiagnosticLog.Error("Unhandled exception", ev.ExceptionObject as Exception);

        // 1. Window first.
        StartupMetrics.Mark("on-startup");
        _vm = new MainViewModel();
        _window = new MainWindow(_vm, paths);
        MainWindow = _window;
        StartupMetrics.Mark("window-created");
        _window.ContentRendered += (_, _) => StartupMetrics.Mark("window-rendered");
        if (!_startHidden) _window.Show();

        ListenForSecondStarts();

        // 2. Data in the background, then compose services on the UI thread.
        StartLoad(paths);
    }

    private void StartLoad(AppPaths paths)
    {
        var init = Task.Run(() => LoadCore(paths));
        init.ContinueWith(t => Dispatcher.BeginInvoke(() => OnCoreLoaded(paths, t)), TaskScheduler.Default);
    }

    private sealed record CoreData(AppPolicy Policy, DataStore Store, AccessResult Access, MigrationRecord? Migration);

    private static CoreData LoadCore(AppPaths paths)
    {
        var migration = new ElectronDataMigrator(paths).RunIfNeeded();
        var policy = PolicyReader.Read(paths.PolicyFile);
        var store = new DataStore(paths);
        store.Load();
        var access = GroupAccessChecker.Check(policy.AllowedGroups);
        StartupMetrics.Mark("data-loaded");
        return new CoreData(policy, store, access, migration);
    }

    private void OnCoreLoaded(AppPaths paths, Task<CoreData> task)
    {
        if (task.Exception is { } ex)
        {
            DiagnosticLog.Error("Startup failed", ex.GetBaseException());
            _vm!.LoadError = ex.GetBaseException().Message;
            _vm.RetryStartup = () => StartLoad(paths); // "Try again" repeats the whole load
            return;
        }
        var core = task.Result;
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(core.Policy);
        services.AddSingleton(core.Store);
        services.AddSingleton(_ => new SessionManager(paths.TmpDir));
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton(sp => new CentralListService(core.Policy.CentralList, paths.CentralCacheDir, () => sp.GetRequiredService<DataStore>().Settings.Defaults));
        services.AddSingleton<IAuthService>(_ => new EntraAuthService(core.Policy.Entra, () => _window is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(_window).Handle));
        services.AddSingleton<SessionTabManager>();
        services.AddSingleton<AppController>();
        services.AddSingleton(_vm!);
        services.AddSingleton<UiFlows>();
        _services = services.BuildServiceProvider();

        var controller = _services.GetRequiredService<AppController>();
        controller.Access = core.Access;
        var tabs = _services.GetRequiredService<SessionTabManager>();
        _window!.AttachTabs(tabs);
        var flows = _services.GetRequiredService<UiFlows>();
        _vm!.Attach(controller, tabs, flows);
        var settings = core.Store.Settings;
        _vm.View = settings.View;
        _vm.FiltersOpen = settings.FiltersOpen;
        _vm.SidebarCollapsed = settings.SidebarCollapsed || _window.ActualWidth is > 0 and < 768;
        WireEvents(controller, tabs);
        _vm.Reload();
        _vm.RaiseAuthChanged();
        flows.ReportMigration(core.Migration);
        // The list is usable once this frame has been rendered.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            StartupMetrics.Mark("list-usable");
            StartupMetrics.WriteLog();
            // Tray, jump list, checks and sign-in only after the list is on screen and the input queue is idle.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => StartBackgroundWork(controller, flows));
            if (_perfReport is not null) _ = new PerfRunner(_vm, _window, _perfReport).RunAsync();
        });
    }

    private void WireEvents(AppController controller, SessionTabManager tabs)
    {
        var d = Dispatcher;
        controller.ConnectionsChanged += () => d.BeginInvoke(() => _vm!.SetConnections(controller.AllConnections()));
        controller.StatusChanged += r => d.BeginInvoke(() => _vm!.ApplyStatus(r));
        controller.RecentChanged += () => d.BeginInvoke(() => { JumpListService.Update(controller); _tray?.UpdateMenu(); });
        controller.Sessions.Updated += s =>
        {
            controller.AuditSessionUpdate(s);
            d.BeginInvoke(() =>
            {
                tabs.OnSessionUpdate(s);
                _vm!.OnSessionUpdate(s);
                _tray?.UpdateMenu();
            });
        };
        controller.Auth.StateChanged += _ => d.BeginInvoke(_vm!.RaiseAuthChanged);
        // Files are written in the background; a failed write must not go unnoticed.
        var writeErrorShown = false;
        controller.Store.WriteFailed += e => d.BeginInvoke(() =>
        {
            _vm!.Notify("error", "Changes could not be saved", e.Message);
            if (writeErrorShown) return;
            writeErrorShown = true;
            DialogWindow.Error("Changes could not be saved",
                "Your latest changes could not be written to disk. They stay available until you close the app. Check free disk space and access to your profile folder, then make the change again.",
                $"{e.GetType().Name}: {e.Message}");
        });
        tabs.Changed += _vm!.RaiseTabs;
    }

    /// <summary>
    /// Second starts (jump list, tray, start menu) are accepted from the first moment; a connect request that
    /// arrives while data is still loading waits in _pendingConnect.
    /// </summary>
    private void ListenForSecondStarts()
    {
        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() =>
        {
            ShowMainWindow();
            if (ConnectArg(args) is not { } id) return;
            if (_backgroundStarted && _services?.GetService<UiFlows>() is { } flows) flows.HandleConnectRequest(id);
            else _pendingConnect = id;
        });
        _instance.Listen();
    }

    private bool _backgroundStarted;

    private void StartBackgroundWork(AppController controller, UiFlows flows)
    {
        if (_backgroundStarted) return;
        _backgroundStarted = true;
        // Separate idle callbacks so that no single step holds the UI thread for long.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _tray = new TrayService(controller, flows, ShowMainWindow, QuitApp);
            flows.Tray = _tray;
        });
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => JumpListService.Update(controller));
        ScheduleStatus(controller);
        _durationTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => { if (_window!.IsVisible) _vm!.TickDurations(); }, Dispatcher);
        _durationTimer.Start();
        if (controller.Access.Allowed)
        {
            _ = controller.RefreshStatusAsync();
            if (controller.Central.Config is not null)
            {
                _ = controller.RefreshCentralAsync();
                var hourly = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => _ = controller.RefreshCentralAsync(), Dispatcher);
                hourly.Start();
            }
        }
        if (controller.Auth.Configured) _ = controller.Auth.SignInSilentAsync();
        _ = CheckUpdateAsync();
        if (_pendingConnect is { } id) { _pendingConnect = null; flows.HandleConnectRequest(id); }
    }

    private async Task CheckUpdateAsync()
    {
        var status = await PackageInfo.CheckForUpdateAsync();
        if (status is UpdateStatus.Available or UpdateStatus.Required)
            _vm!.ShowUpdate(new MainViewModel.UpdateNotice("An update is available", "The new version is installed the next time the app starts."));
    }

    /// <summary>Reachability checks run while the window is visible; a full check also runs when it comes back.</summary>
    public void ScheduleStatus(AppController controller)
    {
        _statusTimer?.Stop();
        var seconds = controller.Store.Settings.StatusRefreshSeconds;
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Background, (_, _) =>
        {
            if (controller.Access.Allowed && _window!.IsVisible && _window.WindowState != WindowState.Minimized) _ = controller.RefreshStatusAsync();
        }, Dispatcher);
        _statusTimer.Start();
    }

    /// <summary>Background work slows down while the window is hidden or minimized and catches up when it comes back.</summary>
    public void OnWindowForeground(bool foreground)
    {
        if (_services?.GetService<AppController>() is not { } controller) return;
        controller.Sessions.SetBackground(!foreground);
        if (foreground && controller.Access.Allowed && DateTimeOffset.UtcNow - controller.LastFullStatusAt > TimeSpan.FromSeconds(controller.Store.Settings.StatusRefreshSeconds))
            _ = controller.RefreshStatusAsync();
    }

    public void ShowMainWindow()
    {
        if (_window is null) return;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    // ── Closing ──────────────────────────────────────────
    private bool _quitting;
    private bool _trayHintShown;

    /// <summary>Closing the window keeps the app in the notification area while sessions are open.</summary>
    public bool OnMainWindowClosing()
    {
        if (_quitting) return true;
        var controller = _services?.GetService<AppController>();
        var live = controller?.Sessions.LiveCount ?? 0;
        if (live > 0 && controller!.Store.Settings.KeepRunningInTray && _tray is not null)
        {
            _window!.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Balloon("The app keeps running in the notification area while your sessions are open.");
            }
            return false;
        }
        if (live > 0 && !ConfirmQuitWithSessions()) return false;
        _quitting = true;
        PrepareQuit();
        Dispatcher.BeginInvoke(Shutdown);
        return true;
    }

    private bool ConfirmQuitWithSessions()
    {
        var controller = _services?.GetService<AppController>();
        var live = controller?.Sessions.LiveCount ?? 0;
        if (live == 0) return true;
        var ok = DialogWindow.Confirm("Sessions are still open", $"{(live == 1 ? "1 remote session is" : $"{live} remote sessions are")} still open.", "Quit app",
            detail: "Separate Remote Desktop windows stay open, but this app stops tracking them. Sessions in tabs are disconnected; they keep running on the server.");
        if (ok)
        {
            controller!.Store.Audit.Write("app_quit_with_open_sessions", new Dictionary<string, object?>
            {
                ["sessions"] = controller.Sessions.List().Where(s => s.IsLive).Select(s => new Dictionary<string, object?> { ["sessionId"] = s.Id, ["host"] = s.Host }).ToList(),
            });
        }
        return ok;
    }

    public void QuitApp()
    {
        if (!ConfirmQuitWithSessions()) return;
        _quitting = true;
        PrepareQuit();
        Shutdown();
    }

    /// <summary>
    /// Before windows close: move tab sessions out of their slots (child windows die with their parent) and ask
    /// them to disconnect, so the remote session is left cleanly (it keeps running on the server).
    /// </summary>
    private void PrepareQuit()
    {
        try
        {
            if (_services?.GetService<SessionTabManager>() is { } tabs) tabs.ParkAll();
            if (_services?.GetService<SessionManager>() is { } sessions)
                foreach (var s in sessions.List().Where(s => s.IsLive && sessions.IsEmbedded(s.Id))) sessions.Disconnect(s.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DiagnosticLog.Error("Quit preparation failed", ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services?.GetService<SessionTabManager>() is { } tabs) tabs.ParkAll();
            if (_services?.GetService<SessionManager>() is { } sessions)
            {
                foreach (var s in sessions.List().Where(s => s.IsLive && sessions.IsEmbedded(s.Id))) sessions.Disconnect(s.Id);
            }
            _window?.SavePlacement();
            _tray?.Dispose();
            _services?.GetService<DataStore>()?.FlushAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) { DiagnosticLog.Error("Exit cleanup failed", ex); }
        DiagnosticLog.Info("Exit");
        DiagnosticLog.Flush(TimeSpan.FromSeconds(2));
        _services?.Dispose();
        base.OnExit(e);
    }

    // ── Errors ───────────────────────────────────────────
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        ReportError("Something went wrong", e.Exception);
    }

    /// <summary>Shows an error in a dialog. Expected errors carry user-facing messages; others get a generic text.</summary>
    public static void ReportError(string title, Exception e)
    {
        var message = e is InvalidOperationException or Core.Validation.ValidationException or IOException or UnauthorizedAccessException or TimeoutException
            ? e.Message
            : "The action could not be completed. Details are in the app log.";
        if (e is not (InvalidOperationException or Core.Validation.ValidationException)) DiagnosticLog.Error(title, e);
        DialogWindow.Error(title, message, $"{e.GetType().Name}: {e.Message}");
    }

    // ── Arguments ────────────────────────────────────────
    private string? ArgValue(string name)
    {
        var i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    private static string? ConnectArg(IEnumerable<string> args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--connect=", StringComparison.Ordinal));
        var id = arg?["--connect=".Length..];
        return id is not null && (Core.Validation.ConnectionNormalizer.IsValidId(id) || Core.Validation.ConnectionNormalizer.IsCentralId(id)) ? id : null;
    }
}
