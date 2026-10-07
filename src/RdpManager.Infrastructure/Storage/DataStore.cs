using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Validation;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.Infrastructure.Storage;

/// <summary>A connections.json that could not be read. It was moved aside, never overwritten.</summary>
public sealed record CorruptFileInfo(string Backup, string Error);

/// <summary>
/// Local persistence of systems, settings and central-system state (port of Store in store.js).
/// Changes apply to memory immediately; files are written on a background thread (atomic, serialized),
/// so the UI never waits for disk I/O. <see cref="FlushAsync"/> is awaited on exit.
/// </summary>
public sealed class DataStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private List<Connection> _connections = [];
    private CentralState _central = new();
    private AppSettings _settings = new();
    private Task _pending = Task.CompletedTask;

    public DataStore(AppPaths paths)
    {
        _paths = paths;
        Audit = new AuditLog(paths.AuditFile, paths.DataDir);
    }

    public AuditLog Audit { get; }
    public CorruptFileInfo? Corrupt { get; private set; }

    /// <summary>Reads all files. Call from a background thread.</summary>
    public void Load()
    {
        Directory.CreateDirectory(_paths.DataDir);
        var connections = LoadConnections();
        var central = JsonFile.TryRead(_paths.CentralStateFile, RdpJsonContext.Default.CentralState) ?? new CentralState();
        var settings = JsonFile.TryRead(_paths.SettingsFile, RdpJsonContext.Default.AppSettings) ?? new AppSettings();
        settings.Defaults ??= new ConnectionDefaults();
        try { settings = SettingsNormalizer.Normalize(settings); }
        catch (ValidationException) { settings = new AppSettings(); }
        lock (_gate)
        {
            _connections = connections;
            _central = central;
            _settings = settings;
        }
    }

    /// <summary>A damaged file is kept as a copy instead of being overwritten by the next save.</summary>
    private List<Connection> LoadConnections()
    {
        if (!File.Exists(_paths.ConnectionsFile)) return [];
        try
        {
            var list = JsonFile.Read(_paths.ConnectionsFile, RdpJsonContext.Default.ListConnection) ?? throw new InvalidDataException("Unexpected file format");
            return list.Where(c => c is not null).ToList();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
        {
            var backup = $"{_paths.ConnectionsFile}.corrupt-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            try
            {
                // Copy first: the damaged original is only removed once an identical copy exists.
                File.Copy(_paths.ConnectionsFile, backup);
                File.Delete(_paths.ConnectionsFile);
            }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException)
            {
                // No copy: keep the original in place and refuse to overwrite it until the user decides.
                backup = _paths.ConnectionsFile;
                _connectionsLocked = true;
                DiagnosticLog.Error("Damaged connections.json could not be copied; saving is blocked", io);
            }
            Corrupt = new CorruptFileInfo(backup, e.Message);
            DiagnosticLog.Error("connections.json could not be read and was moved aside", e);
            return [];
        }
    }

    private volatile bool _connectionsLocked;

    /// <summary>"Start with an empty list": from now on the file may be written again.</summary>
    public void AcknowledgeCorrupt()
    {
        Corrupt = null;
        _connectionsLocked = false;
    }

    // ── Systems ──────────────────────────────────────────
    public IReadOnlyList<Connection> Connections { get { lock (_gate) return _connections.ToList(); } }

    public Connection? Get(string id) { lock (_gate) return _connections.Find(c => c.Id == id); }

    /// <summary>Validates and saves a system (new or existing). Returns the saved copy.</summary>
    public Connection Save(Connection input)
    {
        Connection clean;
        lock (_gate)
        {
            clean = ConnectionNormalizer.Normalize(input, _settings.Defaults);
            clean.Source = null;
            var index = _connections.FindIndex(c => c.Id == clean.Id);
            if (index < 0)
            {
                clean.CreatedAt = ConnectionNormalizer.IsoNow();
                _connections.Add(clean);
            }
            else
            {
                clean.CreatedAt = _connections[index].CreatedAt;
                clean.LastConnectedAt = _connections[index].LastConnectedAt;
                _connections[index] = clean;
            }
        }
        PersistConnections();
        return clean.Clone();
    }

    /// <summary>Saves many systems with one file write (import, samples). Each one is validated.</summary>
    public List<Connection> SaveMany(IEnumerable<Connection> inputs)
    {
        var saved = new List<Connection>();
        lock (_gate)
        {
            foreach (var input in inputs)
            {
                var clean = ConnectionNormalizer.Normalize(input, _settings.Defaults);
                clean.Source = null;
                clean.CreatedAt = ConnectionNormalizer.IsoNow();
                _connections.RemoveAll(c => c.Id == clean.Id);
                _connections.Add(clean);
                saved.Add(clean.Clone());
            }
        }
        PersistConnections();
        return saved;
    }

    public Connection? Patch(string id, Action<Connection> patch)
    {
        Connection? copy;
        lock (_gate)
        {
            var c = _connections.Find(x => x.Id == id);
            if (c is null) return null;
            patch(c);
            copy = c.Clone();
        }
        PersistConnections();
        return copy;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_gate) removed = _connections.RemoveAll(c => c.Id == id) > 0;
        if (removed) PersistConnections();
        return removed;
    }

    // ── Central systems (personal overlay) ───────────────
    public (bool Favorite, string? LastConnectedAt) CentralOverlay(string id)
    {
        lock (_gate)
            return (_central.Favorites.GetValueOrDefault(id), _central.LastConnectedAt.GetValueOrDefault(id));
    }

    public void PatchCentral(string id, bool? favorite = null, string? lastConnectedAt = null)
    {
        lock (_gate)
        {
            if (favorite == true) _central.Favorites[id] = true;
            else if (favorite == false) _central.Favorites.Remove(id);
            if (lastConnectedAt is not null) _central.LastConnectedAt[id] = lastConnectedAt;
        }
        Enqueue(() =>
        {
            CentralState snapshot;
            lock (_gate) snapshot = new CentralState { Favorites = new(_central.Favorites), LastConnectedAt = new(_central.LastConnectedAt) };
            JsonFile.Write(_paths.CentralStateFile, snapshot, RdpJsonContext.Default.CentralState);
        });
    }

    // ── Settings ─────────────────────────────────────────
    public AppSettings Settings { get { lock (_gate) return _settings.Clone(); } }

    public AppSettings SaveSettings(AppSettings input)
    {
        var s = SettingsNormalizer.Normalize(input);
        lock (_gate) _settings = s;
        Enqueue(() =>
        {
            AppSettings snapshot;
            lock (_gate) snapshot = _settings.Clone();
            JsonFile.Write(_paths.SettingsFile, snapshot, RdpJsonContext.Default.AppSettings);
        });
        return s.Clone();
    }

    // ── Background writes ────────────────────────────────
    private void PersistConnections() => Enqueue(() =>
    {
        if (_connectionsLocked) throw new IOException("The damaged system list could not be backed up, so it is not overwritten. Choose \"Start with an empty list\" or ask IT.");
        List<Connection> snapshot;
        lock (_gate) snapshot = _connections.Select(c => c.Clone()).ToList();
        JsonFile.Write(_paths.ConnectionsFile, snapshot, RdpJsonContext.Default.ListConnection);
    });

    /// <summary>Writes run one after another, in order, off the calling thread.</summary>
    private void Enqueue(Action write)
    {
        lock (_gate)
        {
            _pending = _pending.ContinueWith(async _ =>
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try { write(); }
                catch (Exception e) { DiagnosticLog.Error("Saving data failed", e); WriteFailed?.Invoke(e); }
                finally { _writeLock.Release(); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Raised (on a background thread) when a file could not be written.</summary>
    public event Action<Exception>? WriteFailed;

    public Task FlushAsync()
    {
        Task t;
        lock (_gate) t = _pending;
        return Task.WhenAll(t, Audit.FlushAsync());
    }
}
