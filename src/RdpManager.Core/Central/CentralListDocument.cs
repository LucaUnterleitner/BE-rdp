using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Validation;

namespace RdpManager.Core.Central;

/// <summary>A verified central system list (systems.json).</summary>
public sealed record CentralDocument(int Version, string? IssuedAt, string? ExpiresAt, JsonElement Systems);

/// <summary>
/// Central, read-only system list published by IT (port of central.js). The list and a detached Ed25519
/// signature (base64) are verified against the public key from the policy before anything is used.
/// </summary>
public static class CentralListDocument
{
    public const string Schema = "bp-rdp-systems/1";
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Verifies the detached signature and parses the document. Throws with a user-facing message.</summary>
    public static CentralDocument Verify(byte[] bytes, string signatureB64, string publicKeyB64, DateTimeOffset? now = null)
    {
        Ed25519PublicKeyParameters key;
        try
        {
            key = (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(Convert.FromBase64String(publicKeyB64));
        }
        catch (Exception)
        {
            throw new InvalidDataException("The public key for the central system list in the policy is not valid.");
        }
        byte[] signature;
        try { signature = Convert.FromBase64String((signatureB64 ?? "").Trim()); }
        catch (FormatException) { signature = []; }
        var verifier = new Ed25519Signer();
        verifier.Init(false, key);
        verifier.BlockUpdate(bytes, 0, bytes.Length);
        if (signature.Length != 64 || !verifier.VerifySignature(signature))
            throw new InvalidDataException("The signature of the central system list is not valid. The list was not used.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new InvalidDataException("The central system list is not valid JSON."); }
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema)
            throw new InvalidDataException("The central system list has an unknown format.");
        if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var version) || version < 1)
            throw new InvalidDataException("The central system list has no valid version.");
        var expiresAt = root.TryGetProperty("expiresAt", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetString() : null;
        if (expiresAt is not null && DateTimeOffset.TryParse(expiresAt, out var exp) && exp < (now ?? DateTimeOffset.UtcNow))
            throw new InvalidDataException("The central system list has expired. Contact IT.");
        if (!root.TryGetProperty("systems", out var systems) || systems.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The central system list contains no systems.");
        var issuedAt = root.TryGetProperty("issuedAt", out var iss) && iss.ValueKind == JsonValueKind.String ? iss.GetString() : null;
        return new CentralDocument(version, issuedAt, expiresAt, systems.Clone());
    }

    /// <summary>Turns verified entries into read-only connections with namespaced ids. Invalid entries are skipped.</summary>
    public static List<Connection> ToConnections(CentralDocument doc, ConnectionDefaults defaults)
    {
        var o = new List<Connection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in doc.Systems.EnumerateArray().Take(5000))
        {
            if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var id = idEl.GetString()!;
            if (!ConnectionNormalizer.IsValidId(id) || !seen.Add(id)) continue;
            try
            {
                var entry = ConnectionJson.FromJson(raw, defaults);
                if (entry is null) continue;
                entry.Id = "";
                entry.Favorite = false;
                entry.Sample = false;
                entry.LastConnectedAt = null;
                entry.Extra = null;
                var c = ConnectionNormalizer.Normalize(entry, defaults);
                c.Id = "central-" + id.ToLowerInvariant();
                c.Source = "central";
                o.Add(c);
            }
            catch (Exception e) when (e is ValidationException or JsonException or InvalidOperationException)
            {
                // skip invalid entry
            }
        }
        return o;
    }
}
