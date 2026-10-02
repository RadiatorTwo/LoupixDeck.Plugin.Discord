using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Transport;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// Optional trace of every RPC frame in both directions, meant for finding out undocumented
/// commands and events. Secrets are masked before anything is written: tokens, the OAuth code
/// and the client secret never reach the log.
/// </summary>
internal sealed class RpcDebugLog(IPluginLogger logger, Func<bool> enabled)
{
    private const string Mask = "***";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token",
        "refresh_token",
        "client_secret",
        "rpc_token",
        "token",
        // The OAuth authorization code from AUTHORIZE. Error replies use "code" as well, but
        // there it is a number, and only string values are masked.
        "code"
    };

    public bool IsEnabled => enabled();

    public void Outgoing(IpcOpcode opcode, string json) => Write("→", opcode, json);

    public void Incoming(IpcOpcode opcode, string json) => Write("←", opcode, json);

    private void Write(string direction, IpcOpcode opcode, string json)
    {
        if (!enabled()) return;
        logger.Info($"[Discord RPC] {direction} {opcode} {Redact(json)}");
    }

    /// <summary>Returns <paramref name="json"/> with every sensitive string value replaced by <c>***</c>.</summary>
    public static string Redact(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node == null) return json;
            RedactNode(node);
            return node.ToJsonString();
        }
        catch
        {
            // Never fall back to the raw text: it could hold exactly what is meant to be hidden.
            return $"<unparseable payload, {json.Length} chars>";
        }
    }

    private static void RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    JsonNode? value = obj[key];
                    if (value == null) continue;

                    if (SensitiveKeys.Contains(key) && value is JsonValue v && v.TryGetValue(out string? _))
                        obj[key] = Mask;
                    else
                        RedactNode(value);
                }
                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (item != null) RedactNode(item);
                }
                break;
        }
    }
}
