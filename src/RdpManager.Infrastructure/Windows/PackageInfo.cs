using System.Runtime.InteropServices;
using Microsoft.Win32;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.Infrastructure.Windows;

public enum UpdateStatus { NotChecked, NotPackaged, NotAvailable, Available, Required, Unknown }

/// <summary>
/// MSIX package identity, the in-app update indicator and "start with Windows".
/// Packaged: StartupTask extension of the manifest. Unpackaged: HKCU Run key with --hidden.
/// </summary>
public static partial class PackageInfo
{
    public const string StartupTaskId = "BearingPointRemoteDesktopStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BearingPoint Remote Desktop";

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint length, IntPtr name);

    private static bool? _packaged;

    /// <summary>True when the process runs with MSIX package identity.</summary>
    public static bool IsPackaged
    {
        get
        {
            if (_packaged is null)
            {
                uint len = 0;
                const int AppModelErrorNoPackage = 15700;
                _packaged = GetCurrentPackageFullName(ref len, IntPtr.Zero) != AppModelErrorNoPackage;
            }
            return _packaged.Value;
        }
    }

    public static string? PackageFullName => IsPackaged ? global::Windows.ApplicationModel.Package.Current.Id.FullName : null;

    /// <summary>True when Windows started the packaged app through its startup task (sign-in); it then starts in the tray.</summary>
    public static bool IsStartupTaskLaunch()
    {
        if (!IsPackaged) return false;
        try
        {
            return global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind == global::Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("Activation kind could not be read", e);
            return false;
        }
    }

    /// <summary>
    /// Checks for an update when the package was installed from an .appinstaller file. Intune-managed installs
    /// report "Unknown": Intune delivers updates itself.
    /// </summary>
    public static async Task<UpdateStatus> CheckForUpdateAsync()
    {
        if (!IsPackaged) return UpdateStatus.NotPackaged;
        try
        {
            var pm = new global::Windows.Management.Deployment.PackageManager();
            var pkg = pm.FindPackageForUser(string.Empty, global::Windows.ApplicationModel.Package.Current.Id.FullName);
            if (pkg?.GetAppInstallerInfo() is null) return UpdateStatus.Unknown;
            var r = await pkg.CheckUpdateAvailabilityAsync();
            return r.Availability switch
            {
                global::Windows.ApplicationModel.PackageUpdateAvailability.Available => UpdateStatus.Available,
                global::Windows.ApplicationModel.PackageUpdateAvailability.Required => UpdateStatus.Required,
                global::Windows.ApplicationModel.PackageUpdateAvailability.NoUpdates => UpdateStatus.NotAvailable,
                _ => UpdateStatus.Unknown,
            };
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("Update check failed", e);
            return UpdateStatus.Unknown;
        }
    }

    public static async Task<bool> IsStartWithWindowsEnabledAsync()
    {
        try
        {
            if (IsPackaged)
            {
                var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                return task.State is global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("Startup state could not be read", e);
            return false;
        }
    }

    /// <summary>Returns a message when the setting cannot be applied (for example switched off by the user in Task Manager).</summary>
    public static async Task<string?> SetStartWithWindowsAsync(bool enable)
    {
        try
        {
            if (IsPackaged)
            {
                var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                if (!enable) { task.Disable(); return null; }
                var state = await task.RequestEnableAsync();
                return state switch
                {
                    global::Windows.ApplicationModel.StartupTaskState.DisabledByUser => "Start with Windows was switched off in Task Manager or Windows Settings. Switch it on there.",
                    global::Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => "Start with Windows is switched off by your organization.",
                    _ => null,
                };
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --hidden");
            else key.DeleteValue(RunValue, false);
            return null;
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("Startup setting could not be changed", e);
            return "Start with Windows could not be changed.";
        }
    }
}
