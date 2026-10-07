using System.Text.RegularExpressions;
using RdpManager.Core.Models;

namespace RdpManager.Core.Targets;

/// <summary>Validation rules for host names, user names and web addresses (targets.js).</summary>
public static partial class HostRules
{
    // Host names, IPv4 and IPv6. Never starting with "-" or ".", so a host can never be read as a program option.
    [GeneratedRegex("^(?![-.])[A-Za-z0-9._-]{1,253}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostRe();

    [GeneratedRegex("^[0-9A-Fa-f:.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6Re();

    // SSH user names: letters, digits and . _ @ \ - only. No spaces, quotes or shell characters.
    [GeneratedRegex(@"^(?!-)[A-Za-z0-9._@\\-]{1,104}$", RegexOptions.CultureInvariant)]
    private static partial Regex SshUserRe();

    /// <summary>Gateway host names (store.js HOST_PATTERN).</summary>
    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    public static partial Regex GatewayHostRe();

    public static bool IsHost(string? h)
    {
        var s = h ?? "";
        return HostRe().IsMatch(s) || (Ipv6Re().IsMatch(s) && s.Count(c => c == ':') > 1);
    }

    public static bool IsIpv6Literal(string s) => Ipv6Re().IsMatch(s) && s.Count(c => c == ':') > 1;

    public static bool IsSshUser(string? u)
    {
        var s = u ?? "";
        return SshUserRe().IsMatch(s) && !s.EndsWith('\\');
    }

    /// <summary>The URL a web connection opens. Built from validated fields, never from free text.</summary>
    public static string WebUrl(string host, int port, WebOptions? web)
    {
        var scheme = web?.Scheme == "http" ? "http" : "https";
        var h = host.Contains(':') ? $"[{host}]" : host;
        var p = port != 0 && port != Protocols.DefaultPort(Protocols.Web, scheme) ? $":{port}" : "";
        var path = web?.Path ?? "";
        var suffix = path.StartsWith('/') || path.StartsWith('?') ? path : path.Length > 0 ? "/" + path : "";
        return $"{scheme}://{h}{p}{suffix}";
    }

    /// <summary>One line that says what a connection will do, for previews and screen readers.</summary>
    public static string Describe(string protocol, string host, int port, string? username, WebOptions? web)
    {
        if (protocol == Protocols.Web) return $"Opens {WebUrl(host, port, web)} in your browser";
        var p = port != 0 && port != Protocols.DefaultPort(protocol) ? $":{port}" : "";
        var asUser = string.IsNullOrEmpty(username) ? "" : $" as {username}";
        return protocol == Protocols.Ssh ? $"SSH terminal to {host}{p}{asUser}" : $"Remote Desktop to {host}{p}{asUser}";
    }
}
