using System.Text.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Validation;

namespace RdpManager.Core.Policy;

/// <summary>
/// Parses the IT policy file (port of readPolicy in store.js). Only known keys with the right types are used;
/// unknown keys and wrong types are ignored. The owner check of the file happens in the infrastructure layer.
/// </summary>
public static class PolicyParser
{
    public static AppPolicy Parse(string json, string file)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return new AppPolicy { File = file, Active = false }; }
        using (doc) return Parse(doc.RootElement, file);
    }

    public static AppPolicy Parse(JsonElement raw, string file)
    {
        if (raw.ValueKind != JsonValueKind.Object) return new AppPolicy { File = file, Active = false };

        PolicyRedirect? redirect = null;
        if (raw.TryGetProperty("redirect", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            redirect = new PolicyRedirect
            {
                Clipboard = Bool(r, "clipboard"),
                Printers = Bool(r, "printers"),
                Microphone = Bool(r, "microphone"),
                Smartcards = Bool(r, "smartcards"),
                Drives = OneOf(r, "drives", "none", "all"),
                Audio = OneOf(r, "audio", "local", "remote", "none"),
            };
        }

        IReadOnlyList<string>? allowedProtocols = null;
        if (raw.TryGetProperty("allowedProtocols", out var ap) && ap.ValueKind == JsonValueKind.Array)
        {
            var listed = ap.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet();
            allowedProtocols = Protocols.All.Where(listed.Contains).ToList();
        }

        string? ssh = null;
        if (raw.TryGetProperty("ssh", out var s) && s.ValueKind == JsonValueKind.Object) ssh = OneOf(s, "strictHostKeyChecking", "ask", "accept-new", "yes");

        IReadOnlyList<string>? groups = null;
        if (raw.TryGetProperty("allowedGroups", out var g) && g.ValueKind == JsonValueKind.Array)
            groups = g.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(50).ToList();

        PolicyNotice? notice = null;
        if (raw.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object)
        {
            var title = Str(n, "title", 120);
            notice = new PolicyNotice(string.IsNullOrEmpty(title) ? "Notice from IT" : title, Str(n, "message", 500) ?? "");
        }

        var helpUrl = Str(raw, "helpUrl", 300);
        if (helpUrl is not null && !helpUrl.StartsWith("https://", StringComparison.Ordinal)) helpUrl = null;

        CentralListConfig? central = null;
        if (raw.TryGetProperty("centralList", out var c) && c.ValueKind == JsonValueKind.Object && Str(c, "publicKey", 200) is { Length: > 0 } pk)
        {
            var url = Str(c, "url", 500);
            if (url is not null && !url.StartsWith("https://", StringComparison.Ordinal)) url = null;
            var path = url is null ? Str(c, "path", 500) : null;
            if (url is not null || !string.IsNullOrEmpty(path))
            {
                var maxAge = c.TryGetProperty("maxAgeHours", out var ma) && ma.ValueKind == JsonValueKind.Number && ma.GetDouble() > 0 ? ma.GetDouble() : 72;
                central = new CentralListConfig { Url = url, Path = path, PublicKey = pk, MaxAgeHours = maxAge };
            }
        }

        var thumb = Str(raw, "signingThumbprint", 128);
        return new AppPolicy
        {
            File = file,
            Active = true,
            Redirect = redirect,
            AllowSavedCredentials = Bool(raw, "allowSavedCredentials"),
            LaunchMode = OneOf(raw, "launchMode", "file", "direct"),
            RequireCredentialProtection = OneOf(raw, "requireCredentialProtection", "remoteGuard", "restrictedAdmin"),
            SigningThumbprint = string.IsNullOrEmpty(thumb) ? null : SettingsNormalizer.CleanThumbprint(thumb),
            AllowedProtocols = allowedProtocols,
            SshStrictHostKeyChecking = ssh,
            AllowedGroups = groups,
            Notice = notice,
            HelpUrl = helpUrl,
            ServiceDeskName = Str(raw, "serviceDeskName", 120) is { Length: > 0 } sd ? sd : null,
            CentralList = central,
            Entra = ParseEntra(raw),
        };
    }

    /// <summary>
    /// Optional Entra ID section. Tenant and client id must be GUIDs (a single-tenant registration); a tenant
    /// domain name such as contoso.onmicrosoft.com is accepted as well. Anything else disables sign-in.
    /// </summary>
    private static EntraConfig? ParseEntra(JsonElement raw)
    {
        if (!raw.TryGetProperty("entra", out var e) || e.ValueKind != JsonValueKind.Object) return null;
        var tenant = Str(e, "tenantId", 100)?.Trim() ?? "";
        var client = Str(e, "clientId", 100)?.Trim() ?? "";
        var tenantOk = Guid.TryParse(tenant, out _) || (tenant.Contains('.') && tenant.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-'));
        if (!tenantOk || !Guid.TryParse(client, out _)) return null;
        var scopes = e.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array
            ? sc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length is > 0 and < 300).Take(20).ToList()
            : [];
        var roles = e.TryGetProperty("allowedRoles", out var ro) && ro.ValueKind == JsonValueKind.Array
            ? ro.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(50).ToList()
            : [];
        return new EntraConfig
        {
            TenantId = tenant,
            ClientId = client,
            Scopes = scopes.Count > 0 ? scopes : ["User.Read"],
            RequireSignIn = Bool(e, "requireSignIn") ?? false,
            AllowedRoles = roles,
            UseBroker = Bool(e, "useBroker") ?? true,
        };
    }

    private static bool? Bool(JsonElement o, string key)
        => o.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string? Str(JsonElement o, string key, int max)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!;
        return s.Length > max ? s[..max] : s;
    }

    private static string? OneOf(JsonElement o, string key, params string[] allowed)
        => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && allowed.Contains(v.GetString()) ? v.GetString() : null;
}
