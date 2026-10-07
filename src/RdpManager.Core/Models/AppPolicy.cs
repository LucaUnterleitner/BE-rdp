namespace RdpManager.Core.Models;

/// <summary>
/// Machine-wide policy deployed by IT (%ProgramData%\BearingPoint\RdpClient\policy.json).
/// Only known keys with the right types are used; anything else is ignored.
/// </summary>
public sealed class AppPolicy
{
    public string File { get; init; } = "";
    public bool Active { get; init; }
    /// <summary>Set when the file exists but was ignored (for example a wrong owner).</summary>
    public string? Rejected { get; init; }

    /// <summary>Locked device options. Keys: clipboard, printers, microphone, smartcards (bool), drives ("none"/"all"), audio.</summary>
    public PolicyRedirect? Redirect { get; init; }
    public bool? AllowSavedCredentials { get; init; }
    public string? LaunchMode { get; init; }
    public string? RequireCredentialProtection { get; init; }
    public string? SigningThumbprint { get; init; }
    public IReadOnlyList<string>? AllowedProtocols { get; init; }
    public string? SshStrictHostKeyChecking { get; init; }
    public IReadOnlyList<string>? AllowedGroups { get; init; }
    public PolicyNotice? Notice { get; init; }
    public string? HelpUrl { get; init; }
    public string? ServiceDeskName { get; init; }
    public CentralListConfig? CentralList { get; init; }
    public EntraConfig? Entra { get; init; }

    public static AppPolicy None(string file) => new() { File = file };
}

public sealed class PolicyRedirect
{
    public bool? Clipboard { get; init; }
    public bool? Printers { get; init; }
    public bool? Microphone { get; init; }
    public bool? Smartcards { get; init; }
    public string? Drives { get; init; }
    public string? Audio { get; init; }

    public bool Any => Clipboard.HasValue || Printers.HasValue || Microphone.HasValue || Smartcards.HasValue || Drives is not null || Audio is not null;

    public bool IsLocked(string key) => key switch
    {
        "clipboard" => Clipboard.HasValue,
        "printers" => Printers.HasValue,
        "microphone" => Microphone.HasValue,
        "smartcards" => Smartcards.HasValue,
        "drives" => Drives is not null,
        "audio" => Audio is not null,
        _ => false,
    };

    public IEnumerable<(string Key, string Value)> Entries()
    {
        if (Clipboard.HasValue) yield return ("clipboard", Clipboard.Value ? "true" : "false");
        if (Printers.HasValue) yield return ("printers", Printers.Value ? "true" : "false");
        if (Microphone.HasValue) yield return ("microphone", Microphone.Value ? "true" : "false");
        if (Smartcards.HasValue) yield return ("smartcards", Smartcards.Value ? "true" : "false");
        if (Drives is not null) yield return ("drives", Drives);
        if (Audio is not null) yield return ("audio", Audio);
    }
}

public sealed record PolicyNotice(string Title, string Message);

public sealed class CentralListConfig
{
    public string? Url { get; init; }
    public string? Path { get; init; }
    public string PublicKey { get; init; } = "";
    public double MaxAgeHours { get; init; } = 72;
}

/// <summary>
/// Microsoft Entra ID sign-in (optional). Public client: no secret. Values come from the IT policy; the app
/// ships without any tenant or client id.
/// </summary>
public sealed class EntraConfig
{
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public IReadOnlyList<string> Scopes { get; init; } = ["User.Read"];
    /// <summary>When true, the app is usable only after a successful sign-in.</summary>
    public bool RequireSignIn { get; init; }
    /// <summary>App roles (ID token "roles" claim); one of them is required when the list is not empty.</summary>
    public IReadOnlyList<string> AllowedRoles { get; init; } = [];
    /// <summary>Use the Windows broker (WAM). Recommended; set false only where WAM is unavailable.</summary>
    public bool UseBroker { get; init; } = true;
}
