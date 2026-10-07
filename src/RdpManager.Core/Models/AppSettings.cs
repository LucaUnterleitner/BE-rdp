using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Core.Models;

/// <summary>User preferences (settings.json). Same keys as the Electron app; unknown keys are preserved.</summary>
public sealed class AppSettings
{
    /// <summary>"tabs" (sessions inside the app) or "external" (separate mstsc windows).</summary>
    public string SessionWindow { get; set; } = "tabs";
    /// <summary>In tabs: send Windows key combinations such as Alt+Tab to the remote computer.</summary>
    public bool KeysToRemote { get; set; }
    /// <summary>External windows: "file" (all settings, Windows security confirmation) or "direct" (mstsc /v:).</summary>
    public string LaunchMode { get; set; } = "file";
    /// <summary>SHA-256 thumbprint of an internal code-signing certificate for rdpsign.exe.</summary>
    public string SigningThumbprint { get; set; } = "";
    public int StatusRefreshSeconds { get; set; } = 60;
    public bool ConfirmDisconnect { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool KeepRunningInTray { get; set; } = true;
    /// <summary>"grid" or "list" (native app; the Electron app kept this in localStorage).</summary>
    public string View { get; set; } = "grid";
    public bool FiltersOpen { get; set; }
    public bool SidebarCollapsed { get; set; }
    public ConnectionDefaults Defaults { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public AppSettings Clone()
    {
        var s = (AppSettings)MemberwiseClone();
        s.Defaults = Defaults.Clone();
        s.Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra);
        return s;
    }
}

/// <summary>Personal state for read-only central systems (central-state.json).</summary>
public sealed class CentralState
{
    public Dictionary<string, bool> Favorites { get; set; } = [];
    public Dictionary<string, string> LastConnectedAt { get; set; } = [];
}
