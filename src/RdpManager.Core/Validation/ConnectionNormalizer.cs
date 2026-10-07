using System.Globalization;
using System.Text.RegularExpressions;
using RdpManager.Core.Models;
using RdpManager.Core.Targets;

namespace RdpManager.Core.Validation;

/// <summary>A user-facing validation error (the message is shown as is).</summary>
public sealed class ValidationException(string message) : Exception(message);

/// <summary>
/// Validates and cleans a connection before it is saved or started (port of normalizeConnection in store.js).
/// Everything that later reaches an .rdp file or a command line passes through here.
/// </summary>
public static partial class ConnectionNormalizer
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    public static partial Regex IdRe();

    [GeneratedRegex("^central-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    public static partial Regex CentralIdRe();

    [GeneratedRegex(@"[\u0000-\u001f\u007f]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlRe();

    [GeneratedRegex(@"^[^\s""/\[\]:;|=,+*?<>]+$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserRe();

    [GeneratedRegex(@"^[/?][\x21-\x7e]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WebPathRe();

    [GeneratedRegex(@"^[A-Za-z]:\\[^""%&|<>^!()\u0000-\u001f]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyFileRe();

    [GeneratedRegex(@"^(\[[0-9A-Fa-f:.]+\]|[^:]+)(?::(\d{1,5}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex JumpHostRe();

    public static bool IsValidId(string? id) => id is not null && IdRe().IsMatch(id);
    public static bool IsCentralId(string? id) => id is not null && CentralIdRe().IsMatch(id);

    /// <summary>Returns a validated copy. Throws <see cref="ValidationException"/> with a user-facing message.</summary>
    public static Connection Normalize(Connection input, ConnectionDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(input);
        var d = defaults.Clone();
        var protocol = string.IsNullOrEmpty(input.Protocol) ? Protocols.Rdp : input.Protocol;
        if (!Protocols.All.Contains(protocol))
            throw new ValidationException($"The connection type \"{Truncate(protocol, 20)}\" is not supported by this app version.");
        var host = (input.Host ?? "").Trim();
        if (host.Length == 0) throw new ValidationException("Enter a computer name or IP address.");
        if (!HostRules.IsHost(host))
        {
            throw new ValidationException(host.Contains(':') && !host.Contains("::")
                ? "Enter the port in the Port field, not in the computer name."
                : "The computer name contains characters that are not allowed.");
        }
        var web = new WebOptions { Scheme = input.Web?.Scheme == "http" ? "http" : "https", Path = (input.Web?.Path ?? "").Trim() };
        var port = input.Port != 0 ? input.Port : Protocols.DefaultPort(protocol, web.Scheme);
        if (port < 1 || port > 65535) throw new ValidationException("The port must be between 1 and 65535.");
        var username = ControlRe().Replace((input.Username ?? "").Trim(), "");
        if (protocol == Protocols.Ssh)
        {
            if (username.Length > 0 && !HostRules.IsSshUser(username))
                throw new ValidationException("The username may only contain letters, digits and . _ @ \\ - and must not start with \"-\".");
        }
        else if (username.Length > 0 && !WindowsUserRe().IsMatch(RemoveFirstBackslash(username)))
        {
            throw new ValidationException("The username format is not valid. Use DOMAIN\\username or username@domain.");
        }
        var ssh = NormalizeSsh(input.Ssh, protocol == Protocols.Ssh);
        if (protocol == Protocols.Web)
        {
            if (web.Path.Length > 0 && !WebPathRe().IsMatch(web.Path)) throw new ValidationException("The path must start with / and must not contain spaces.");
            if (!Uri.TryCreate(HostRules.WebUrl(host, port, web), UriKind.Absolute, out var url)
                || url.Scheme != web.Scheme
                || !string.Equals(url.Host.Trim('[', ']'), host, StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException("The web address is not valid.");
            }
        }
        var gwMode = input.Gateway?.Mode ?? d.Gateway.Mode;
        var gwHost = (input.Gateway?.Host ?? d.Gateway.Host ?? "").Trim();
        if (gwMode != "none" && gwHost.Length == 0) throw new ValidationException("Enter an RD Gateway address or switch the gateway off.");
        if (gwMode != "none" && !HostRules.GatewayHostRe().IsMatch(gwHost)) throw new ValidationException("The RD Gateway address contains characters that are not allowed.");

        var security = (input.Security ?? d.Security).Clone();
        security.AuthLevel = input.Security?.AuthLevel == 1 ? 1 : 2;
        security.CredentialProtection = input.Security?.CredentialProtection is "none" or "remoteGuard" or "restrictedAdmin"
            ? input.Security.CredentialProtection : "none";

        var display = (input.Display ?? d.Display).Clone();
        if (display.Mode is not ("fullscreen" or "window")) display.Mode = "fullscreen";
        var redirect = (input.Redirect ?? d.Redirect).Clone();
        if (redirect.Drives is not ("none" or "all")) redirect.Drives = "none";
        if (redirect.Audio is not ("local" or "remote" or "none")) redirect.Audio = "local";

        var name = string.IsNullOrEmpty(input.Name) ? host : input.Name;
        return new Connection
        {
            Id = IsValidId(input.Id) ? input.Id.ToLowerInvariant() : Guid.NewGuid().ToString(),
            Name = Truncate(name.Trim(), 120),
            Protocol = protocol,
            Host = host,
            Port = port,
            Username = username,
            Folder = Truncate((input.Folder ?? "").Trim(), 80),
            Os = Truncate((input.Os ?? "").Trim(), 80),
            Location = Truncate((input.Location ?? "").Trim(), 80),
            Tags = (input.Tags ?? []).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Take(12).ToList(),
            Description = Truncate((input.Description ?? "").Trim(), 500),
            Favorite = input.Favorite,
            Sample = input.Sample,
            CredentialMode = input.CredentialMode == "saved" ? "saved" : "prompt",
            Ssh = ssh,
            Web = web,
            Display = display,
            Redirect = redirect,
            Gateway = new GatewayOptions { Mode = gwMode is "none" or "always" or "detect" ? gwMode : "none", Host = gwHost },
            Security = security,
            LastConnectedAt = IsIsoDate(input.LastConnectedAt) ? input.LastConnectedAt : null,
            CreatedAt = input.CreatedAt,
            Source = input.Source,
            Extra = input.Extra,
            Adhoc = input.Adhoc,
            PromptAlways = input.PromptAlways,
        };
    }

    /// <summary>
    /// SSH options. Everything here ends up on the ssh.exe command line, so only strict values pass: a key file as
    /// an absolute path without shell characters, and a jump host as [user@]host[:port].
    /// </summary>
    public static SshOptions NormalizeSsh(SshOptions? input, bool check)
    {
        var identityFile = (input?.IdentityFile ?? "").Trim();
        var jumpHost = (input?.JumpHost ?? "").Trim();
        if (check && identityFile.Length > 0 && (!KeyFileRe().IsMatch(identityFile) || identityFile.Length > 260))
            throw new ValidationException("The key file must be a full path such as C:\\Users\\name\\.ssh\\id_ed25519, without the characters \" % & | < > ^ ! ( ).");
        if (check && jumpHost.Length > 0)
        {
            var at = jumpHost.LastIndexOf('@');
            var user = at > 0 ? jumpHost[..at] : "";
            var m = JumpHostRe().Match(at > 0 ? jumpHost[(at + 1)..] : jumpHost);
            var jh = m.Success ? m.Groups[1].Value.Trim('[', ']') : "";
            var jp = m.Success && m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 22;
            if (!m.Success || !HostRules.IsHost(jh) || (user.Length > 0 && !HostRules.IsSshUser(user)) || jp < 1 || jp > 65535)
                throw new ValidationException("Enter the jump host as host, host:port or user@host:port.");
        }
        return new SshOptions { IdentityFile = Truncate(identityFile, 260), JumpHost = Truncate(jumpHost, 300) };
    }

    /// <summary>Defaults for new systems, reduced to known values (normalizeDefaults).</summary>
    public static ConnectionDefaults NormalizeDefaults(ConnectionDefaults? input)
    {
        var src = input ?? new ConnectionDefaults();
        var c = Normalize(new Connection
        {
            Host = "defaults.invalid", Display = src.Display, Redirect = src.Redirect, Gateway = src.Gateway, Security = src.Security,
        }, new ConnectionDefaults());
        return new ConnectionDefaults
        {
            Display = c.Display!,
            Redirect = c.Redirect!,
            Gateway = c.Gateway!.Mode != "none" && c.Gateway.Host.Length > 0 ? c.Gateway : new GatewayOptions(),
            Security = c.Security!,
        };
    }

    /// <summary>Splits a comma-separated tag text ("a, b ,,c").</summary>
    public static List<string> ParseTags(string? text)
        => (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Take(12).ToList();

    public static bool IsIsoDate(string? s)
        => !string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    /// <summary>ISO 8601 UTC with milliseconds, the format JavaScript's toISOString() writes.</summary>
    public static string IsoNow() => IsoOf(DateTimeOffset.UtcNow);

    public static string IsoOf(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string RemoveFirstBackslash(string s)
    {
        var i = s.IndexOf('\\');
        return i < 0 ? s : s.Remove(i, 1);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
