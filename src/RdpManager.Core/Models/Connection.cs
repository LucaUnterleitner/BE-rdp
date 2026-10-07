using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Core.Models;

/// <summary>
/// A saved system. The JSON shape is identical to the Electron app's connections.json, so files can move
/// between both apps. Unknown properties are kept in <see cref="Extra"/> and written back unchanged.
/// No secrets are ever stored here; passwords live only in Windows Credential Manager.
/// </summary>
public sealed class Connection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = Protocols.Rdp;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Os { get; set; } = "";
    public string Location { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Description { get; set; } = "";
    public bool Favorite { get; set; }
    public bool Sample { get; set; }
    public string CredentialMode { get; set; } = "prompt";
    public SshOptions? Ssh { get; set; }
    public WebOptions? Web { get; set; }
    public DisplayOptions? Display { get; set; }
    public RedirectOptions? Redirect { get; set; }
    public GatewayOptions? Gateway { get; set; }
    public SecurityOptions? Security { get; set; }
    public string? LastConnectedAt { get; set; }
    public string? CreatedAt { get; set; }

    /// <summary>"central" for read-only systems from the IT list; not persisted in connections.json.</summary>
    public string? Source { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    // Runtime-only flags (never written).
    [JsonIgnore] public bool Adhoc { get; set; }
    [JsonIgnore] public bool PromptAlways { get; set; }
    [JsonIgnore] public bool IsCentral => Source == "central";

    public Connection Clone()
    {
        var c = (Connection)MemberwiseClone();
        c.Tags = [.. Tags];
        c.Ssh = Ssh?.Clone();
        c.Web = Web?.Clone();
        c.Display = Display?.Clone();
        c.Redirect = Redirect?.Clone();
        c.Gateway = Gateway?.Clone();
        c.Security = Security?.Clone();
        c.Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra);
        return c;
    }
}

public sealed class SshOptions
{
    public string IdentityFile { get; set; } = "";
    public string JumpHost { get; set; } = "";
    public SshOptions Clone() => (SshOptions)MemberwiseClone();
}

public sealed class WebOptions
{
    public string Scheme { get; set; } = "https";
    public string Path { get; set; } = "";
    public WebOptions Clone() => (WebOptions)MemberwiseClone();
}

public sealed class DisplayOptions
{
    /// <summary>"fullscreen" or "window" (separate Remote Desktop windows only).</summary>
    public string Mode { get; set; } = "fullscreen";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool Multimon { get; set; }
    public bool DynamicResolution { get; set; } = true;
    public bool SmartSizing { get; set; }
    public DisplayOptions Clone() => (DisplayOptions)MemberwiseClone();
}

public sealed class RedirectOptions
{
    public bool Clipboard { get; set; } = true;
    /// <summary>"none" or "all".</summary>
    public string Drives { get; set; } = "none";
    public bool Printers { get; set; }
    /// <summary>"local", "remote" or "none".</summary>
    public string Audio { get; set; } = "local";
    public bool Microphone { get; set; }
    public bool Smartcards { get; set; }
    public RedirectOptions Clone() => (RedirectOptions)MemberwiseClone();
}

public sealed class GatewayOptions
{
    /// <summary>"none", "always" or "detect".</summary>
    public string Mode { get; set; } = "none";
    public string Host { get; set; } = "";
    public GatewayOptions Clone() => (GatewayOptions)MemberwiseClone();
}

public sealed class SecurityOptions
{
    public bool AdminSession { get; set; }
    /// <summary>"none", "remoteGuard" or "restrictedAdmin".</summary>
    public string CredentialProtection { get; set; } = "none";
    /// <summary>2: warn when the server identity cannot be verified; 1: do not connect.</summary>
    public int AuthLevel { get; set; } = 2;
    public SecurityOptions Clone() => (SecurityOptions)MemberwiseClone();
}

/// <summary>Option groups that new systems start with (Settings → Defaults for new systems).</summary>
public sealed class ConnectionDefaults
{
    public DisplayOptions Display { get; set; } = new();
    public RedirectOptions Redirect { get; set; } = new();
    public GatewayOptions Gateway { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();

    public ConnectionDefaults Clone() => new()
    {
        Display = Display.Clone(), Redirect = Redirect.Clone(), Gateway = Gateway.Clone(), Security = Security.Clone(),
    };
}
