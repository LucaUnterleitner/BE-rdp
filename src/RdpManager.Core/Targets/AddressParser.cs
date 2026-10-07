using System.Text.RegularExpressions;
using RdpManager.Core.Models;

namespace RdpManager.Core.Targets;

/// <summary>A connection target parsed from typed text ("ssh user@host -p 2222", "https://ilo01", "srv01:3389").</summary>
public sealed record ParsedTarget(string Protocol, string Host, int Port, string Username, string Scheme, string Path, bool Explicit);

/// <summary>Parsing of typed addresses for quick connect and the edit dialog (port of targets.js).</summary>
public static partial class AddressParser
{
    [GeneratedRegex(@"^\[([0-9A-Fa-f:.]+)\](?::(\d{1,5}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketRe();

    [GeneratedRegex(@"^([^:]+)(?::(\d{1,5}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPortRe();

    [GeneratedRegex(@"^(ssh|rdp|web)\s+(.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeywordRe();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRe();

    [GeneratedRegex(@"^\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortRe();

    [GeneratedRegex(@"[\u0000-\u001f\u007f]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlRe();

    private sealed record HostPort(string Host, int? Port, string Username = "");

    /// <summary>"host", "host:port", "[v6]:port" or a bare IPv6 address. Null when the text is not an address.</summary>
    private static HostPort? ParseHostPort(string text)
    {
        var s = text ?? "";
        var m = BracketRe().Match(s);
        if (m.Success) return HostRules.IsHost(m.Groups[1].Value) ? new HostPort(m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null) : null;
        if (HostRules.IsIpv6Literal(s)) return HostRules.IsHost(s) ? new HostPort(s, null) : null;
        m = HostPortRe().Match(s);
        if (!m.Success || !HostRules.IsHost(m.Groups[1].Value)) return null;
        int? port = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
        if (port is not null && (port < 1 || port > 65535)) return null;
        return new HostPort(m.Groups[1].Value, port);
    }

    /// <summary>"user@host[:port]" (the last @ separates the host, so user@domain@host works).</summary>
    private static HostPort? ParseUserHostPort(string s)
    {
        var at = s.LastIndexOf('@');
        if (at <= 0) return null;
        var user = s[..at];
        var hp = ParseHostPort(s[(at + 1)..]);
        return hp is not null && HostRules.IsSshUser(user) ? hp with { Username = user } : null;
    }

    private static ParsedTarget Target(string protocol, string host, int? port, bool isExplicit, string username = "", string scheme = "https", string path = "")
        => new(protocol, host, port ?? Protocols.DefaultPort(protocol, scheme), username, scheme, path, isExplicit);

    private static ParsedTarget? ParseSshCommand(string rest)
    {
        var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int? port = null;
        var user = "";
        string? dest = null;
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t == "-p" && i + 1 < tokens.Length && PortRe().IsMatch(tokens[i + 1])) { port = int.Parse(tokens[++i]); continue; }
            if (t == "-l" && i + 1 < tokens.Length && HostRules.IsSshUser(tokens[i + 1])) { user = tokens[++i]; continue; }
            if (dest is not null || t.StartsWith('-')) return null;
            dest = t;
        }
        if (dest is null) return null;
        var parsed = ParseUserHostPort(dest) ?? ParseHostPort(dest);
        if (parsed is null) return null;
        return Target(Protocols.Ssh, parsed.Host, port ?? parsed.Port, true, user.Length > 0 ? user : parsed.Username);
    }

    private static ParsedTarget? ParseUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        var scheme = url.Scheme.ToLowerInvariant();
        var host = url.Host.Trim('[', ']');
        if (!HostRules.IsHost(host)) return null;
        int? port = url.IsDefaultPort ? null : url.Port;
        if (scheme is "http" or "https")
        {
            var path = (url.AbsolutePath == "/" ? "" : url.AbsolutePath) + url.Query;
            return Target(Protocols.Web, host, port, true, scheme: scheme, path: path.Length > 500 ? path[..500] : path);
        }
        var user = Uri.UnescapeDataString(url.UserInfo.Split(':')[0]);
        if (scheme == "ssh") return user.Length > 0 && !HostRules.IsSshUser(user) ? null : Target(Protocols.Ssh, host, port, true, user);
        if (scheme == "rdp") return Target(Protocols.Rdp, host, port, true);
        return null;
    }

    /// <summary>
    /// Possible targets for typed text, most likely first. Explicit forms (ssh …, rdp …, web …, URLs) give one
    /// target; a bare host gives RDP first with SSH and Web as alternatives.
    /// </summary>
    public static IReadOnlyList<ParsedTarget> ParseTargets(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0 || ControlRe().IsMatch(text)) return [];
        var kw = KeywordRe().Match(text);
        if (kw.Success)
        {
            var p = kw.Groups[1].Value.ToLowerInvariant();
            if (p == Protocols.Ssh) { var t = ParseSshCommand(kw.Groups[2].Value); return t is null ? [] : [t]; }
            var inner = ParseTargets(kw.Groups[2].Value).FirstOrDefault(t => t.Protocol == p);
            if (inner is not null) return [inner with { Explicit = true }];
            var hp0 = ParseHostPort(kw.Groups[2].Value);
            return hp0 is null ? [] : [Target(p, hp0.Host, hp0.Port, true)];
        }
        if (SchemeRe().IsMatch(text)) { var t = ParseUrl(text); return t is null ? [] : [t]; }

        var uhp = ParseUserHostPort(text);
        if (uhp is not null) return [Target(Protocols.Ssh, uhp.Host, uhp.Port, false, uhp.Username), Target(Protocols.Rdp, uhp.Host, null, false, uhp.Username)];
        var hp = ParseHostPort(text);
        if (hp is null) return [];
        if (hp.Port == 22) return [Target(Protocols.Ssh, hp.Host, hp.Port, false)];
        if (hp.Port is 80 or 8080) return [Target(Protocols.Web, hp.Host, hp.Port, false, scheme: "http")];
        if (hp.Port is 443 or 8443) return [Target(Protocols.Web, hp.Host, hp.Port, false)];
        if (hp.Port is not null) return [Target(Protocols.Rdp, hp.Host, hp.Port, false)];
        return [Target(Protocols.Rdp, hp.Host, null, false), Target(Protocols.Ssh, hp.Host, null, false), Target(Protocols.Web, hp.Host, null, false)];
    }

    /// <summary>
    /// Targets limited to the enabled connection types. When a port suggests a switched-off type
    /// ("server01:22" while SSH is off), the address is used for RDP with that port.
    /// </summary>
    public static IReadOnlyList<ParsedTarget> EnabledTargets(string? input, IReadOnlyList<string>? enabled = null)
    {
        enabled ??= Protocols.Enabled;
        var all = ParseTargets(input).Where(t => enabled.Contains(t.Protocol)).ToList();
        if (all.Count > 0 || !enabled.Contains(Protocols.Rdp)) return all;
        var hp = ParseHostPort((input ?? "").Trim());
        return hp is null ? [] : [Target(Protocols.Rdp, hp.Host, hp.Port, false)];
    }
}
