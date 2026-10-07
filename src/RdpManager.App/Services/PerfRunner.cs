using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RdpManager.App.ViewModels;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.App.Services;

/// <summary>
/// Scripted measurements for PERFORMANCE.md (started with --perf-report &lt;file&gt;, only in unpackaged builds and
/// meant to run with --data-dir pointing at generated test data). Every timing includes layout and rendering:
/// the clock stops when the dispatcher reaches ContextIdle after the action. The app exits afterwards.
/// </summary>
public sealed class PerfRunner(MainViewModel vm, Window window, string reportFile)
{
    private readonly Dictionary<string, object> _results = [];

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

    private async Task<double> TimeAsync(Action action, int runs = 5)
    {
        var samples = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            await Idle();
            var sw = Stopwatch.StartNew();
            action();
            await Idle();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return Math.Round(samples[samples.Count / 2], 1); // median
    }

    private static Dictionary<string, double> Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return new Dictionary<string, double>
        {
            ["workingSetMB"] = Math.Round(p.WorkingSet64 / 1048576.0, 1),
            ["privateMB"] = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1),
            ["managedHeapMB"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
        };
    }

    public async Task RunAsync()
    {
        try
        {
            await Task.Delay(1500); // let deferred startup work settle
            await Idle();
            _results["startupMs"] = StartupMetrics.All;
            _results["systems"] = vm.Items.Count;
            _results["memoryAfterStart"] = Memory();

            vm.Navigate("systems");
            await Idle();
            _results["searchTypingMs"] = await TimeAsync(() => { vm.Query = "perftest-04"; vm.ApplySearch(); });
            _results["searchNoMatchMs"] = await TimeAsync(() => { vm.Query = "zzz-no-match"; vm.ApplySearch(); });
            _results["searchClearMs"] = await TimeAsync(() => { vm.Query = ""; vm.ApplySearch(); });
            _results["filterStatusOfflineMs"] = await TimeAsync(() => { vm.FilterStatus = "offline"; vm.FilterStatus = ""; });
            _results["filterOsMs"] = await TimeAsync(() => { vm.FilterOs = "Windows Server 2022"; });
            vm.ClearFilters();
            _results["switchToListViewMs"] = await TimeAsync(() => { vm.SetViewCommand.Execute(vm.View == "list" ? "grid" : "list"); });
            vm.SetViewCommand.Execute("grid");
            await Idle();

            var views = new Dictionary<string, double>();
            foreach (var route in new[] { "dashboard", "systems", "favorites", "recent", "settings", "help" })
                views[route] = await TimeAsync(() => vm.Navigate(route), 3);
            if (vm.Items.Count > 0)
            {
                var id = vm.Items[vm.Items.Count / 2].Id;
                views["details"] = await TimeAsync(() => vm.OpenDetails(id), 3);
                vm.ClosePageTab(id);
            }
            _results["openViewMs"] = views;

            // Typical use: scroll through the whole list once.
            vm.Navigate("systems");
            await Idle();
            var scroller = FindScrollViewer(window);
            if (scroller is not null)
            {
                var sw = Stopwatch.StartNew();
                var steps = 0;
                while (scroller.VerticalOffset < scroller.ScrollableHeight && steps < 400)
                {
                    scroller.ScrollToVerticalOffset(scroller.VerticalOffset + scroller.ViewportHeight);
                    await Idle();
                    steps++;
                }
                _results["scrollWholeListMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                _results["scrollPages"] = steps;
                _results["scrollMsPerPage"] = steps > 0 ? Math.Round(sw.Elapsed.TotalMilliseconds / steps, 1) : 0;
            }
            vm.Navigate("dashboard");
            await Idle();
            _results["memoryAfterUse"] = Memory();
            _results["machine"] = new Dictionary<string, object>
            {
                ["os"] = Environment.OSVersion.VersionString,
                ["cpus"] = Environment.ProcessorCount,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["time"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            };
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("Performance run failed", e);
            await File.WriteAllTextAsync(reportFile, $"{{\"error\": {JsonSerializer.Serialize(e.ToString())}}}");
        }
        Application.Current.Shutdown();
    }


    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is System.Windows.Controls.ScrollViewer { ScrollableHeight: > 0 } sv && sv.CanContentScroll) return sv;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }
}
