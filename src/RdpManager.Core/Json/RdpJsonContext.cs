using System.Text.Json;
using System.Text.Json.Serialization;
using RdpManager.Core.Models;

namespace RdpManager.Core.Json;

/// <summary>Result of the one-time import of the Electron app's data (migration.json).</summary>
public sealed class MigrationRecord
{
    public string Status { get; set; } = "";
    public string Source { get; set; } = "";
    public string Backup { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public int Imported { get; set; }
    public int Merged { get; set; }
    public int Rejected { get; set; }
    public bool SettingsImported { get; set; }
    public bool AuditImported { get; set; }
    public string? Error { get; set; }
}

public sealed class RejectedRecord
{
    public string Reason { get; set; } = "";
    public JsonElement Record { get; set; }
}

/// <summary>Window size and position, per computer (%LOCALAPPDATA%).</summary>
public sealed class WindowPlacementRecord
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Source-generated serialization (no reflection at startup). camelCase and 2-space indentation match the files
/// the Electron app writes; numbers given as strings (for example in central lists) are accepted.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Connection))]
[JsonSerializable(typeof(List<Connection>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CentralState))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(MigrationRecord))]
[JsonSerializable(typeof(List<RejectedRecord>))]
[JsonSerializable(typeof(WindowPlacementRecord))]
[JsonSerializable(typeof(DisplayOptions))]
[JsonSerializable(typeof(RedirectOptions))]
[JsonSerializable(typeof(GatewayOptions))]
[JsonSerializable(typeof(SecurityOptions))]
public sealed partial class RdpJsonContext : JsonSerializerContext;
