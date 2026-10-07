using System.Text.Json;
using System.Text.Json.Nodes;
using RdpManager.Core.Models;

namespace RdpManager.Core.Json;

/// <summary>
/// Reads a connection record from JSON and fills missing option fields from the defaults, field by field, the
/// way the Electron app merges them ({ ...defaults.redirect, ...record.redirect }). A central entry that sets
/// only "printers" therefore keeps the least-privilege defaults for everything else.
/// </summary>
public static class ConnectionJson
{
    public static Connection? FromJson(JsonElement raw, ConnectionDefaults defaults)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        var node = JsonNode.Parse(raw.GetRawText())!.AsObject();
        Merge(node, "display", JsonSerializer.SerializeToNode(defaults.Display, RdpJsonContext.Default.DisplayOptions));
        Merge(node, "redirect", JsonSerializer.SerializeToNode(defaults.Redirect, RdpJsonContext.Default.RedirectOptions));
        Merge(node, "gateway", JsonSerializer.SerializeToNode(defaults.Gateway, RdpJsonContext.Default.GatewayOptions));
        Merge(node, "security", JsonSerializer.SerializeToNode(defaults.Security, RdpJsonContext.Default.SecurityOptions));
        return node.Deserialize(RdpJsonContext.Default.Connection);
    }

    private static void Merge(JsonObject record, string key, JsonNode? defaults)
    {
        if (defaults is not JsonObject merged) return;
        if (record[key] is JsonObject own)
        {
            foreach (var (k, v) in own) merged[k] = v?.DeepClone();
        }
        record[key] = merged;
    }
}
