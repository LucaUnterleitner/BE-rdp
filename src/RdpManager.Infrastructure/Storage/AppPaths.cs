namespace RdpManager.Infrastructure.Storage;

/// <summary>
/// File locations. User data roams with the profile (%APPDATA%), caches, logs and temporary files stay local.
/// The MSIX package disables AppData write virtualization, so these are the real folders in both modes.
/// </summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string LocalDir { get; }
    /// <summary>Data folder of the Electron app (read only, source of the migration).</summary>
    public string LegacyDataDir { get; }
    public string PolicyFile { get; }
    public bool IsOverride { get; }

    public string ConnectionsFile => Path.Combine(DataDir, "connections.json");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string CentralStateFile => Path.Combine(DataDir, "central-state.json");
    public string AuditFile => Path.Combine(DataDir, "audit.log");
    public string MigrationFile => Path.Combine(DataDir, "migration.json");
    public string MigrationRejectedFile => Path.Combine(DataDir, "migration-rejected.json");
    public string MigrationBackupRoot => Path.Combine(DataDir, "migration-backup");
    public string TmpDir => Path.Combine(LocalDir, "tmp");
    public string CentralCacheDir => Path.Combine(LocalDir, "central-cache");
    public string LogsDir => Path.Combine(LocalDir, "logs");
    public string WindowPlacementFile => Path.Combine(LocalDir, "window.json");

    private AppPaths(string dataDir, string localDir, string legacyDir, string policyFile, bool isOverride)
    {
        DataDir = dataDir;
        LocalDir = localDir;
        LegacyDataDir = legacyDir;
        PolicyFile = policyFile;
        IsOverride = isOverride;
    }

    /// <param name="overrideDir">
    /// Development and measurement only (ignored when the app runs from its MSIX package): a separate folder
    /// for data, local files and the "Electron" source, so test data never mixes with real user data.
    /// </param>
    public static AppPaths Create(string? overrideDir = null)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var policy = Path.Combine(string.IsNullOrEmpty(programData) ? @"C:\ProgramData" : programData, "BearingPoint", "RdpClient", "policy.json");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            var root = Path.GetFullPath(overrideDir);
            return new AppPaths(Path.Combine(root, "data"), Path.Combine(root, "local"), Path.Combine(root, "electron"), policy, true);
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return new AppPaths(
            Path.Combine(appData, "BearingPoint", "RemoteDesktop"),
            Path.Combine(local, "BearingPoint", "RemoteDesktop"),
            Path.Combine(appData, "BearingPoint", "RdpClient"),
            policy,
            false);
    }

    /// <summary>Explicit paths, for tests.</summary>
    public static AppPaths ForTest(string root, string? policyFile = null)
        => new(Path.Combine(root, "data"), Path.Combine(root, "local"), Path.Combine(root, "electron"), policyFile ?? Path.Combine(root, "policy.json"), true);
}
