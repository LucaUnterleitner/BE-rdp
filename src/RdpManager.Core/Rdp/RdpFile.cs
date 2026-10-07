using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RdpManager.Core.Models;

namespace RdpManager.Core.Rdp;

/// <summary>Imported .rdp file: a connection draft plus warnings for the import preview.</summary>
public sealed record RdpImportResult(Connection Connection, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds and parses Microsoft .rdp files ("name:type:value" per line) and mstsc command lines (port of rdpfile.js).
/// Reference: https://learn.microsoft.com/azure/virtual-desktop/rdp-properties
/// </summary>
public static partial class RdpFile
{
    [GeneratedRegex(@"[\u0000-\u001f\u007f]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlRe();

    [GeneratedRegex(@"^([^:]+):([sib]):(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyRe();

    [GeneratedRegex(@"^\[([^\]]+)\](?::(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketRe();

    [GeneratedRegex(@"^(.*?)(?::(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPortRe();

    [GeneratedRegex(@"\.rdp$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RdpExtRe();

    /// <summary>Applies IT policy locks on top of a connection's settings. Returns a new object.</summary>
    public static Connection ApplyPolicy(Connection conn, AppPolicy? policy)
    {
        var o = conn.Clone();
        var r = o.Redirect ??= new RedirectOptions();
        var lck = policy?.Redirect;
        if (lck is not null)
        {
            if (lck.Clipboard.HasValue) r.Clipboard = lck.Clipboard.Value;
            if (lck.Printers.HasValue) r.Printers = lck.Printers.Value;
            if (lck.Microphone.HasValue) r.Microphone = lck.Microphone.Value;
            if (lck.Smartcards.HasValue) r.Smartcards = lck.Smartcards.Value;
            if (lck.Drives is not null) r.Drives = lck.Drives;
            if (lck.Audio is not null) r.Audio = lck.Audio;
        }
        o.Security ??= new SecurityOptions();
        if (!string.IsNullOrEmpty(policy?.RequireCredentialProtection) && o.Security.CredentialProtection == "none")
            o.Security.CredentialProtection = policy.RequireCredentialProtection;
        return o;
    }

    /// <summary>Produces .rdp text for a connection. No secrets are ever written.</summary>
    public static string Build(Connection conn)
    {
        var host = (conn.Host ?? "").Trim();
        var port = conn.Port == 0 ? 3389 : conn.Port;
        var d = conn.Display ?? new DisplayOptions();
        var r = conn.Redirect ?? new RedirectOptions();
        var g = conn.Gateway ?? new GatewayOptions();
        var s = conn.Security ?? new SecurityOptions();
        var sb = new StringBuilder(1024);
        // Values never contain line breaks or control characters, so no extra .rdp properties can be injected.
        void Add(string name, char type, object value)
            => sb.Append(name).Append(':').Append(type).Append(':')
                 .Append(ControlRe().Replace(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", "")).Append("\r\n");

        Add("full address", 's', FormatAddress(host, port));
        if (!string.IsNullOrEmpty(conn.Username) && !conn.PromptAlways) Add("username", 's', conn.Username);
        Add("prompt for credentials on client", 'i', 1);
        if (conn.PromptAlways) Add("prompt for credentials", 'i', 1);
        Add("enablecredsspsupport", 'i', 1);
        Add("authentication level", 'i', s.AuthLevel == 1 ? 1 : 2);
        Add("negotiate security layer", 'i', 1);
        Add("administrative session", 'i', s.AdminSession ? 1 : 0);
        Add("disableconnectionsharing", 'i', 0);
        Add("autoreconnection enabled", 'i', 1);
        Add("bandwidthautodetect", 'i', 1);
        Add("networkautodetect", 'i', 1);
        Add("compression", 'i', 1);

        Add("screen mode id", 'i', d.Mode == "window" ? 1 : 2);
        Add("use multimon", 'i', d.Multimon ? 1 : 0);
        if (d.Mode == "window" && d.Width > 0 && d.Height > 0)
        {
            Add("desktopwidth", 'i', Clamp(d.Width, 200, 8192));
            Add("desktopheight", 'i', Clamp(d.Height, 200, 8192));
        }
        Add("dynamic resolution", 'i', d.DynamicResolution ? 1 : 0);
        Add("smart sizing", 'i', d.SmartSizing ? 1 : 0);
        Add("session bpp", 'i', 32);

        // Redirection (least privilege defaults)
        Add("redirectclipboard", 'i', r.Clipboard ? 1 : 0);
        Add("drivestoredirect", 's', r.Drives == "all" ? "*" : "");
        Add("redirectprinters", 'i', r.Printers ? 1 : 0);
        Add("audiomode", 'i', r.Audio switch { "remote" => 1, "none" => 2, _ => 0 });
        Add("audiocapturemode", 'i', r.Microphone ? 1 : 0);
        Add("redirectsmartcards", 'i', r.Smartcards ? 1 : 0);
        Add("redirectcomports", 'i', 0);
        Add("redirectlocation", 'i', 0);
        Add("keyboardhook", 'i', 2);

        var usage = g.Mode switch { "always" => 1, "detect" => 2, _ => 0 };
        if (usage != 0 && !string.IsNullOrEmpty(g.Host))
        {
            Add("gatewayhostname", 's', g.Host);
            Add("gatewayusagemethod", 'i', usage);
            Add("gatewaycredentialssource", 'i', 4);
            Add("gatewayprofileusagemethod", 'i', 1);
            Add("promptcredentialonce", 'i', 1);
        }
        else
        {
            Add("gatewayusagemethod", 'i', 4);
            Add("gatewayprofileusagemethod", 'i', 0);
        }
        return sb.ToString();
    }

    /// <summary>Encodes as UTF-16 LE with BOM, the encoding mstsc writes.</summary>
    public static byte[] Encode(string text)
    {
        var body = Encoding.Unicode.GetBytes(text);
        var buf = new byte[body.Length + 2];
        buf[0] = 0xFF; buf[1] = 0xFE;
        body.CopyTo(buf, 2);
        return buf;
    }

    public static string Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return Encoding.Unicode.GetString(buffer[2..]);
        if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return Encoding.UTF8.GetString(buffer[3..]);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>Parses .rdp text into a property map (integers for type i, strings otherwise).</summary>
    public static Dictionary<string, object> ParseProperties(string text)
    {
        var props = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var m = PropertyRe().Match(raw.Trim());
            if (!m.Success) continue;
            var name = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[3].Value;
            props[name] = m.Groups[2].Value == "i"
                ? (int.TryParse(LeadingInt(value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : int.MinValue)
                : value;
        }
        return props;
    }

    /// <summary>
    /// Converts an imported .rdp file into a connection draft plus warnings.
    /// Imported files are untrusted: stored passwords and alternate shells are dropped.
    /// </summary>
    public static RdpImportResult ToConnection(string text, string? fileName)
    {
        var p = ParseProperties(text);
        var warnings = new List<string>();
        var (host, port) = ParseAddress(Str(p, "full address"), Int(p, "server port"));

        if (Str(p, "password 51").Length > 0) warnings.Add("A stored password was found in the file and was discarded.");
        if (Str(p, "alternate shell").Length > 0 || Str(p, "remoteapplicationprogram").Length > 0) warnings.Add("A start program (alternate shell / RemoteApp) was found and was not imported.");
        if (Str(p, "drivestoredirect").Length > 0) warnings.Add($"The file requested local drive access ({Str(p, "drivestoredirect")}). It is imported as switched off.");
        if (Int(p, "authentication level") == 0) warnings.Add("The file disabled server identity checks. The app uses \"warn\" instead.");
        if (Int(p, "enablecredsspsupport") == 0) warnings.Add("The file disabled Network Level Authentication. NLA stays on.");

        var usage = Int(p, "gatewayusagemethod");
        var conn = new Connection
        {
            Name = RdpExtRe().Replace(string.IsNullOrEmpty(fileName) ? host : fileName, ""),
            Host = host,
            Port = port,
            Username = Str(p, "username"),
            Display = new DisplayOptions
            {
                Mode = Int(p, "screen mode id") == 1 ? "window" : "fullscreen",
                Width = Int(p, "desktopwidth") is int w and > 0 ? w : 1920,
                Height = Int(p, "desktopheight") is int h and > 0 ? h : 1080,
                Multimon = Int(p, "use multimon") == 1,
                DynamicResolution = Int(p, "dynamic resolution") != 0,
                SmartSizing = Int(p, "smart sizing") == 1,
            },
            Redirect = new RedirectOptions
            {
                Clipboard = Int(p, "redirectclipboard") != 0,
                Drives = "none",
                Printers = Int(p, "redirectprinters") == 1,
                Audio = Int(p, "audiomode") switch { 1 => "remote", 2 => "none", _ => "local" },
                Microphone = Int(p, "audiocapturemode") == 1,
                Smartcards = Int(p, "redirectsmartcards") == 1,
            },
            Gateway = new GatewayOptions { Mode = usage == 1 ? "always" : usage == 2 ? "detect" : "none", Host = Str(p, "gatewayhostname") },
            Security = new SecurityOptions
            {
                AdminSession = Int(p, "administrative session") == 1,
                CredentialProtection = "none",
                AuthLevel = Int(p, "authentication level") == 1 ? 1 : 2,
            },
        };
        if (host.Length == 0) warnings.Add("The file does not contain a computer address.");
        return new RdpImportResult(conn, warnings);
    }

    /// <summary>Command-line arguments for mstsc.exe when launching without a file (no security dialog).</summary>
    public static List<string> BuildDirectArgs(Connection conn)
    {
        var host = (conn.Host ?? "").Trim();
        var port = conn.Port == 0 ? 3389 : conn.Port;
        var d = conn.Display ?? new DisplayOptions();
        var s = conn.Security ?? new SecurityOptions();
        var args = new List<string> { $"/v:{FormatAddress(host, port)}" };
        if (d.Mode == "window")
        {
            if (d.Width > 0 && d.Height > 0) { args.Add($"/w:{Clamp(d.Width, 200, 8192)}"); args.Add($"/h:{Clamp(d.Height, 200, 8192)}"); }
        }
        else
        {
            args.Add("/f");
        }
        if (d.Multimon) args.Add("/multimon");
        if (s.AdminSession) args.Add("/admin");
        if (conn.Gateway is { Mode: not "none" } gw && !string.IsNullOrEmpty(gw.Host)) args.Add($"/g:{gw.Host}");
        if (conn.PromptAlways) args.Add("/prompt");
        args.AddRange(ProtectionArgs(conn));
        return args;
    }

    public static IEnumerable<string> ProtectionArgs(Connection conn) => conn.Security?.CredentialProtection switch
    {
        "remoteGuard" => ["/remoteGuard"],
        "restrictedAdmin" => ["/restrictedAdmin"],
        _ => [],
    };

    /// <summary>host:port, with IPv6 literals in brackets; the port is left out when it is 3389.</summary>
    public static string FormatAddress(string host, int port)
    {
        var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return port == 3389 ? h : $"{h}:{port}";
    }

    /// <summary>Parses "host", "host:port", "[v6]:port" or a bare IPv6 literal.</summary>
    public static (string Host, int Port) ParseAddress(string? address, int? serverPort = null)
    {
        var a = (address ?? "").Trim();
        var fallback = serverPort is > 0 ? serverPort.Value : 3389;
        var bracket = BracketRe().Match(a);
        if (bracket.Success) return (bracket.Groups[1].Value, bracket.Groups[2].Success && int.TryParse(bracket.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bp) ? bp : fallback);
        if (a.Count(c => c == ':') > 1) return (a, fallback);
        var m = HostPortRe().Match(a);
        return (m.Groups[1].Value, m.Groups[2].Success && int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var p) ? p : fallback);
    }

    private static string LeadingInt(string v)
    {
        var t = v.Trim();
        var end = 0;
        while (end < t.Length && (char.IsAsciiDigit(t[end]) || (end == 0 && t[end] == '-'))) end++;
        return t[..end];
    }

    private static string Str(Dictionary<string, object> p, string key) => p.TryGetValue(key, out var v) && v is string s ? s : "";
    private static int? Int(Dictionary<string, object> p, string key) => p.TryGetValue(key, out var v) && v is int i && i != int.MinValue ? i : null;
    private static int Clamp(int v, int min, int max) => Math.Min(max, Math.Max(min, v));
}
