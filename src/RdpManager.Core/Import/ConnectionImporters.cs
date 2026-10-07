using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RdpManager.Core.Models;

namespace RdpManager.Core.Import;

public sealed record ImportItem(Connection Connection, IReadOnlyList<string> Warnings);

public sealed record ImportParseResult(string Format, IReadOnlyList<ImportItem> Items, IReadOnlyList<string> Warnings);

/// <summary>
/// Import of connection lists from Remote Desktop Connection Manager (.rdg) and mRemoteNG (confCons.xml)
/// (port of importers.js). Imported files are untrusted: stored passwords and start programs are never imported,
/// local drive redirection is imported as off, and server identity checks are never weakened.
/// </summary>
public static partial class ConnectionImporters
{
    public const int MaxXmlChars = 5 * 1024 * 1024;

    [GeneratedRegex("<!DOCTYPE|<!ENTITY", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DtdRe();

    [GeneratedRegex(@"<RDCMan[\s>]", RegexOptions.CultureInvariant)]
    private static partial Regex RdcManRe();

    [GeneratedRegex(@"<Connections[\s>]", RegexOptions.CultureInvariant)]
    private static partial Regex MremoteRe();

    [GeneratedRegex(@"(\d+)\s*x\s*(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SizeRe();

    [GeneratedRegex(@"^Res(\d+)x(\d+)$|^(\d+)x(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRe();

    private static XDocument ParseXml(string text)
    {
        if (text.Length > MaxXmlChars) throw new FormatException("The file is too large.");
        // No DTDs: they enable entity expansion attacks and are never needed for these formats.
        if (DtdRe().IsMatch(text)) throw new FormatException("The file contains a document type definition and is not imported.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlChars,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"The file is not valid XML ({ex.Message}).", ex);
        }
    }

    private static bool Bool(string? v) => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    private static string Text(XElement? e) => e?.Value.Trim() ?? "";
    private static XElement? Child(XElement? e, string name) => e?.Element(name);

    // ── RDCMan ────────────────────────────────────────────
    private static readonly string[] RdgBlocks = ["logonCredentials", "connectionSettings", "gatewaySettings", "remoteDesktop", "localResources", "securitySettings"];

    /// <summary>Settings blocks of a node. Schema 3 keeps them next to &lt;properties&gt;, schema 1 inside it.</summary>
    private static Dictionary<string, XElement> RdgBlocksOf(XElement node)
    {
        var o = new Dictionary<string, XElement>();
        foreach (var b in RdgBlocks)
        {
            var el = node.Element(b) ?? node.Element("properties")?.Element(b);
            if (el is null) continue;
            var empty = !el.HasAttributes && !el.HasElements && el.Value.Length == 0;
            if (empty || (string?)el.Attribute("inherit") == "FromParent") continue;
            o[b] = el;
        }
        return o;
    }

    public static ImportParseResult ParseRdg(string text)
    {
        var doc = ParseXml(text);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "RDCMan" || root.Element("file") is null)
            throw new FormatException("This is not a Remote Desktop Connection Manager (.rdg) file.");
        var results = new List<ImportItem>();
        var fileWarnings = new HashSet<string>();

        void Walk(XElement node, List<string> path, Dictionary<string, XElement> inherited)
        {
            var blocks = new Dictionary<string, XElement>(inherited);
            foreach (var kv in RdgBlocksOf(node)) blocks[kv.Key] = kv.Value;
            foreach (var server in node.Elements("server"))
            {
                var own = new Dictionary<string, XElement>(blocks);
                foreach (var kv in RdgBlocksOf(server)) own[kv.Key] = kv.Value;
                results.Add(RdgServer(server, path, own, fileWarnings));
            }
            foreach (var group in node.Elements("group"))
            {
                var name = Text(group.Element("properties")?.Element("name"));
                Walk(group, [.. path, name.Length > 0 ? name : "Group"], blocks);
            }
        }
        Walk(root.Element("file")!, [], []);
        return new ImportParseResult("RDCMan", results, [.. fileWarnings]);
    }

    private static ImportItem RdgServer(XElement server, List<string> path, Dictionary<string, XElement> b, HashSet<string> fileWarnings)
    {
        var p = server.Element("properties");
        var host = Text(Child(p, "name"));
        var warnings = new List<string>();
        b.TryGetValue("logonCredentials", out var cred);
        b.TryGetValue("connectionSettings", out var conn);
        b.TryGetValue("gatewaySettings", out var gw);
        b.TryGetValue("remoteDesktop", out var rd);
        b.TryGetValue("localResources", out var lr);
        b.TryGetValue("securitySettings", out var sec);

        if (Text(Child(cred, "password")).Length > 0) warnings.Add("A stored password was found and was discarded.");
        if (Text(Child(gw, "password")).Length > 0) warnings.Add("A stored gateway password was found and was discarded.");
        if (Text(Child(conn, "startProgram")).Length > 0) warnings.Add("A start program was found and was not imported.");
        if (Child(lr, "redirectDrives") is { } rdr && Bool(rdr.Value)) warnings.Add("The file requested local drive access. It is imported as switched off.");
        var auth = Text(Child(sec, "authentication"));
        if (auth.Contains("none", StringComparison.OrdinalIgnoreCase)) warnings.Add("The file disabled server identity checks. The app uses \"warn\" instead.");
        if (warnings.Any(w => w.Contains("password", StringComparison.Ordinal))) fileWarnings.Add("Passwords from the file are never imported. Save them again in the app if needed.");

        var user = Text(Child(cred, "userName"));
        var domain = Text(Child(cred, "domain"));
        var size = SizeRe().Match(Text(Child(rd, "size")));
        var audio = Text(Child(lr, "audioRedirection")) switch { "Client" => "local", "Remote" => "remote", "NoSound" => "none", _ => "local" };
        var clipboardEl = Child(lr, "redirectClipboard");

        var c = new Connection
        {
            Name = Text(Child(p, "displayName")) is { Length: > 0 } dn ? dn : host,
            Host = host,
            Port = int.TryParse(Text(Child(conn, "port")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 ? port : 3389,
            Username = JoinUser(domain, user),
            Folder = string.Join(" / ", path),
            Description = Text(Child(p, "comment")),
            Display = new DisplayOptions
            {
                Mode = Bool(Child(rd, "fullScreen")?.Value) ? "fullscreen" : size.Success ? "window" : "fullscreen",
                Width = size.Success ? Num(size.Groups[1].Value, 1920) : 1920,
                Height = size.Success ? Num(size.Groups[2].Value, 1080) : 1080,
                DynamicResolution = true,
            },
            Redirect = new RedirectOptions
            {
                Clipboard = clipboardEl is null || Bool(clipboardEl.Value),
                Drives = "none",
                Printers = Bool(Child(lr, "redirectPrinters")?.Value),
                Audio = audio,
                Smartcards = Bool(Child(lr, "redirectSmartCards")?.Value),
            },
            Gateway = Bool(Child(gw, "enabled")?.Value) && Text(Child(gw, "hostName")).Length > 0
                ? new GatewayOptions { Mode = Bool(Child(gw, "localBypass")?.Value) ? "detect" : "always", Host = Text(Child(gw, "hostName")) }
                : new GatewayOptions(),
            Security = new SecurityOptions
            {
                AdminSession = Bool(Child(conn, "connectToConsole")?.Value),
                CredentialProtection = "none",
                AuthLevel = Regex.IsMatch(auth, "^(none|0)$", RegexOptions.IgnoreCase) ? 2 : Regex.IsMatch(auth, "^(fail|1)$", RegexOptions.IgnoreCase) ? 1 : 2,
            },
        };
        return new ImportItem(c, warnings);
    }

    // ── mRemoteNG ─────────────────────────────────────────
    private static readonly string[] MrInheritable = ["Username", "Domain", "Port", "RDGatewayUsageMethod", "RDGatewayHostname", "Resolution",
        "RedirectClipboard", "RedirectPrinters", "RedirectSound", "RedirectSmartCards", "UseConsoleSession", "RDPAuthenticationLevel"];

    /// <param name="protocols">Connection types to import; others are counted as skipped.</param>
    public static ImportParseResult ParseMremote(string text, IReadOnlyList<string>? protocols = null)
    {
        protocols ??= Protocols.Enabled;
        var doc = ParseXml(text);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Connections") throw new FormatException("This is not an mRemoteNG connection file (confCons.xml).");
        if (Bool((string?)root.Attribute("FullFileEncryption")))
            throw new FormatException("The file is fully encrypted. Export it from mRemoteNG without full-file encryption, then import it again.");
        var results = new List<ImportItem>();
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sawPassword = false;

        Dictionary<string, string?> Resolve(XElement node, Dictionary<string, string?>? parent)
        {
            var values = new Dictionary<string, string?>();
            foreach (var f in MrInheritable)
                values[f] = Bool((string?)node.Attribute("Inherit" + f)) && parent is not null ? parent[f] : (string?)node.Attribute(f);
            return values;
        }

        void Walk(IEnumerable<XElement> nodes, List<string> path, Dictionary<string, string?>? parentValues)
        {
            var order = 0;
            foreach (var node in nodes)
            {
                order++;
                var values = Resolve(node, parentValues);
                var type = (string?)node.Attribute("Type");
                if (type == "Container")
                {
                    var name = Attr(node, "Name");
                    Walk(node.Elements("Node"), [.. path, name.Length > 0 ? name : "Folder"], values);
                    continue;
                }
                if (type != "Connection") continue;
                var proto = Attr(node, "Protocol").ToUpperInvariant();
                var warnings = new List<string>();
                if (Attr(node, "Password").Length > 0 || Attr(node, "RDGatewayPassword").Length > 0)
                {
                    warnings.Add("A stored password was found and was discarded.");
                    sawPassword = true;
                }
                if (proto == "RDP")
                {
                    if (Bool((string?)node.Attribute("RedirectDiskDrives"))) warnings.Add("The file requested local drive access. It is imported as switched off.");
                    results.Add(new ImportItem(MremoteRdp(node, values, path), warnings));
                }
                else if (proto is "SSH2" or "SSH1" && protocols.Contains(Protocols.Ssh))
                {
                    if (proto == "SSH1") warnings.Add("SSH version 1 is outdated and insecure. The connection uses SSH version 2.");
                    var putty = Attr(node, "PuttySession");
                    if (putty.Length > 0 && putty != "Default Settings") warnings.Add("PuTTY session settings are not imported.");
                    var c = MremoteBase(node, values, path);
                    c.Protocol = Protocols.Ssh;
                    c.Username = (values["Username"] ?? "").Trim();
                    c.Port = ParsePort(values["Port"]) ?? 22;
                    results.Add(new ImportItem(c, warnings));
                }
                else if (proto is "HTTP" or "HTTPS" && protocols.Contains(Protocols.Web))
                {
                    var scheme = proto == "HTTP" ? "http" : "https";
                    var c = MremoteBase(node, values, path);
                    c.Protocol = Protocols.Web;
                    c.Username = "";
                    c.Port = ParsePort(values["Port"]) ?? (scheme == "http" ? 80 : 443);
                    c.Web = new WebOptions { Scheme = scheme, Path = "" };
                    results.Add(new ImportItem(c, warnings));
                }
                else
                {
                    var key = proto.Length > 0 ? proto : "unknown";
                    skipped[key] = skipped.GetValueOrDefault(key) + 1;
                }
            }
        }
        Walk(root.Elements("Node"), [], null);

        var fileWarnings = new List<string>();
        if (sawPassword) fileWarnings.Add("Passwords from the file are never imported. Save them again in the app if needed.");
        var total = skipped.Values.Sum();
        if (total > 0)
        {
            fileWarnings.Add($"{total} connection{(total == 1 ? "" : "s")} with unsupported types were skipped ({string.Join(", ", skipped.Select(kv => $"{kv.Key}: {kv.Value}"))}).");
        }
        return new ImportParseResult("mRemoteNG", results, fileWarnings);
    }

    /// <summary>Digits from a regex match; values too large for int fall back instead of throwing.</summary>
    private static int Num(string digits, int fallback) => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static string Attr(XElement e, string name) => ((string?)e.Attribute(name) ?? "").Trim();

    private static int? ParsePort(string? v) => int.TryParse((v ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : null;

    private static string JoinUser(string domain, string user)
        => user.Length == 0 ? "" : domain.Length > 0 && !user.Contains('\\') && !user.Contains('@') ? $"{domain}\\{user}" : user;

    private static Connection MremoteBase(XElement node, Dictionary<string, string?> v, List<string> path)
    {
        var host = Attr(node, "Hostname");
        return new Connection
        {
            Name = Attr(node, "Name") is { Length: > 0 } n ? n : host,
            Host = host,
            Username = JoinUser((v["Domain"] ?? "").Trim(), (v["Username"] ?? "").Trim()),
            Folder = string.Join(" / ", path),
            Description = Attr(node, "Descr"),
        };
    }

    private static Connection MremoteRdp(XElement node, Dictionary<string, string?> v, List<string> path)
    {
        var c = MremoteBase(node, v, path);
        var res = (v["Resolution"] ?? "").Trim();
        var size = ResolutionRe().Match(res);
        var w = size.Success ? Num(size.Groups[1].Success ? size.Groups[1].Value : size.Groups[3].Value, 1920) : 1920;
        var h = size.Success ? Num(size.Groups[2].Success ? size.Groups[2].Value : size.Groups[4].Value, 1080) : 1080;
        var gwMode = (v["RDGatewayUsageMethod"] ?? "").Trim() switch { "Always" => "always", "Detect" => "detect", _ => "none" };
        var gwHost = (v["RDGatewayHostname"] ?? "").Trim();
        c.Port = ParsePort(v["Port"]) ?? 3389;
        c.Display = new DisplayOptions
        {
            Mode = size.Success ? "window" : "fullscreen",
            Width = w,
            Height = h,
            DynamicResolution = true,
            SmartSizing = res.Contains("SmartSize", StringComparison.OrdinalIgnoreCase),
        };
        c.Redirect = new RedirectOptions
        {
            Clipboard = v["RedirectClipboard"] is null || Bool(v["RedirectClipboard"]),
            Drives = "none",
            Printers = Bool(v["RedirectPrinters"]),
            Audio = (v["RedirectSound"] ?? "").Trim() switch { "LeaveAtRemoteComputer" => "remote", "DoNotPlay" => "none", _ => "local" },
            Smartcards = Bool(v["RedirectSmartCards"]),
        };
        c.Gateway = gwMode != "none" && gwHost.Length > 0 ? new GatewayOptions { Mode = gwMode, Host = gwHost } : new GatewayOptions();
        c.Security = new SecurityOptions
        {
            AdminSession = Bool(v["UseConsoleSession"]),
            CredentialProtection = "none",
            AuthLevel = (v["RDPAuthenticationLevel"] ?? "").Trim() == "AuthRequired" ? 1 : 2,
        };
        return c;
    }

    /// <summary>Picks the importer by content.</summary>
    public static ImportParseResult DetectAndParse(string text)
    {
        if (RdcManRe().IsMatch(text)) return ParseRdg(text);
        if (MremoteRe().IsMatch(text)) return ParseMremote(text);
        throw new FormatException("The file format is not supported. Use .rdp, RDCMan (.rdg) or mRemoteNG (confCons.xml) files.");
    }
}
