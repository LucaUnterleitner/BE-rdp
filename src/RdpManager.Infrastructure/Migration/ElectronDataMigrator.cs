using System.Globalization;
using System.Text.Json;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Validation;
using RdpManager.Infrastructure.Logging;
using RdpManager.Infrastructure.Storage;

namespace RdpManager.Infrastructure.Migration;

/// <summary>
/// One-time takeover of the Electron app's data (%APPDATA%\BearingPoint\RdpClient). The source folder is only
/// read, never changed. Order: back up → convert and validate → merge and write atomically → verify → mark done.
/// Only the final marker makes the migration complete, so an interrupted run simply repeats.
/// </summary>
public sealed class ElectronDataMigrator(AppPaths paths)
{
    public const string Completed = "completed";
    public const string Failed = "failed";

    private static readonly string[] SourceFiles = ["connections.json", "settings.json", "central-state.json", "audit.log", "audit.1.log"];

    public MigrationRecord? LastRecord => JsonFile.TryRead(paths.MigrationFile, RdpJsonContext.Default.MigrationRecord);

    public bool SourceExists => File.Exists(Path.Combine(paths.LegacyDataDir, "connections.json")) || File.Exists(Path.Combine(paths.LegacyDataDir, "settings.json"));

    /// <summary>Runs at startup (background thread) when there is Electron data and no completed migration.</summary>
    public MigrationRecord? RunIfNeeded()
    {
        var last = LastRecord;
        if (last is not null) return last.Status == Completed || last.Status == Failed ? last : Run();
        return SourceExists ? Run() : null;
    }

    /// <summary>Runs the migration (again). Existing native data is merged, never replaced.</summary>
    public MigrationRecord Run()
    {
        var record = new MigrationRecord { Source = paths.LegacyDataDir };
        try
        {
            Directory.CreateDirectory(paths.DataDir);
            record.Backup = Backup();

            var legacyConnections = Path.Combine(paths.LegacyDataDir, "connections.json");
            var legacySettingsFile = Path.Combine(paths.LegacyDataDir, "settings.json");
            var legacySettings = JsonFile.TryRead(legacySettingsFile, RdpJsonContext.Default.AppSettings);
            var defaults = SafeDefaults(legacySettings);

            var imported = new List<Connection>();
            var rejected = new List<RejectedRecord>();
            if (File.Exists(legacyConnections))
            {
                using var doc = ReadArray(legacyConnections);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        var c = ConnectionJson.FromJson(el, defaults) ?? throw new ValidationException("Empty record.");
                        var clean = ConnectionNormalizer.Normalize(c, defaults);
                        if (!ConnectionNormalizer.IsValidId(c.Id)) throw new ValidationException("The record has no valid id.");
                        clean.CreatedAt = c.CreatedAt;
                        clean.Source = null;
                        imported.Add(clean);
                    }
                    catch (Exception e) when (e is ValidationException or JsonException or InvalidOperationException)
                    {
                        rejected.Add(new RejectedRecord { Reason = e.Message, Record = el.Clone() });
                    }
                }
            }

            // Merge with data the native app may already have (a repeated run never duplicates or overwrites).
            var existing = JsonFile.Read(paths.ConnectionsFile, RdpJsonContext.Default.ListConnection) ?? [];
            var ids = existing.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = imported.Where(c => ids.Add(c.Id)).ToList();
            record.Imported = added.Count;
            record.Merged = imported.Count - added.Count;
            record.Rejected = rejected.Count;
            if (added.Count > 0) JsonFile.Write(paths.ConnectionsFile, existing.Concat(added).ToList(), RdpJsonContext.Default.ListConnection);
            if (rejected.Count > 0) JsonFile.Write(paths.MigrationRejectedFile, rejected, RdpJsonContext.Default.ListRejectedRecord);

            if (legacySettings is not null && !File.Exists(paths.SettingsFile))
            {
                var s = SettingsNormalizer.Normalize(legacySettings);
                JsonFile.Write(paths.SettingsFile, s, RdpJsonContext.Default.AppSettings);
                record.SettingsImported = true;
            }
            CopyIfMissing("central-state.json", paths.CentralStateFile);
            if (!File.Exists(paths.AuditFile) && File.Exists(Path.Combine(paths.LegacyDataDir, "audit.log")))
            {
                CopyIfMissing("audit.1.log", Path.Combine(paths.DataDir, "audit.1.log"));
                CopyIfMissing("audit.log", paths.AuditFile);
                record.AuditImported = true;
            }

            Verify(imported);
            record.Status = Completed;
            DiagnosticLog.Info($"Electron data migrated: {record.Imported} imported, {record.Merged} already present, {record.Rejected} rejected");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            record.Status = Failed;
            record.Error = e is JsonException
                ? $"The system list of the previous app version is damaged and could not be read ({e.Message}). A copy is kept in {record.Backup}."
                : e.Message;
            DiagnosticLog.Error("Electron data migration failed", e);
        }
        record.CompletedAt = ConnectionNormalizer.IsoNow();
        try { JsonFile.Write(paths.MigrationFile, record, RdpJsonContext.Default.MigrationRecord); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiagnosticLog.Error("Migration marker could not be written", e); }
        return record;
    }

    private static JsonDocument ReadArray(string file)
    {
        var doc = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            doc.Dispose();
            throw new InvalidDataException("connections.json of the previous app version does not contain a list of systems.");
        }
        return doc;
    }

    private static ConnectionDefaults SafeDefaults(AppSettings? s)
    {
        try { return ConnectionNormalizer.NormalizeDefaults(s?.Defaults); }
        catch (ValidationException) { return new ConnectionDefaults(); }
    }

    /// <summary>Copies all Electron data files to migration-backup\&lt;timestamp&gt; and checks the copies.</summary>
    private string Backup()
    {
        var dir = Path.Combine(paths.MigrationBackupRoot, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        foreach (var name in SourceFiles)
        {
            var src = Path.Combine(paths.LegacyDataDir, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(dir, name);
            File.Copy(src, dst, true);
            if (new FileInfo(dst).Length != new FileInfo(src).Length) throw new IOException($"The backup of {name} is incomplete.");
        }
        return dir;
    }

    private void CopyIfMissing(string legacyName, string target)
    {
        var src = Path.Combine(paths.LegacyDataDir, legacyName);
        if (File.Exists(src) && !File.Exists(target)) File.Copy(src, target);
    }

    /// <summary>Reads the written file back and checks that every imported system is in it.</summary>
    private void Verify(List<Connection> imported)
    {
        if (imported.Count == 0) return;
        var written = JsonFile.Read(paths.ConnectionsFile, RdpJsonContext.Default.ListConnection) ?? [];
        var ids = written.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = imported.Count(c => !ids.Contains(c.Id));
        if (missing > 0) throw new InvalidDataException($"{missing} migrated systems are missing after writing. Run the import again in Settings.");
    }
}
