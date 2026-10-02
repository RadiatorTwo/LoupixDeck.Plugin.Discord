using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// Settings-page tool for probing undocumented RPC commands: sends one command with JSON
/// arguments and reports Discord's answer. <c>4002 Invalid command</c> means the command does not
/// exist; any other reply means it does, and argument errors usually name the expected fields.
/// </summary>
internal sealed class RpcTester(DiscordSession session, IPluginLogger logger, Func<string, string> tr)
{
    private const int MaxReplyLength = 400;

    public async Task<string> SendAsync(string? command, string? argsJson, string? evt)
    {
        command = command?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(command))
            return tr("Enter an RPC command and save the settings first");

        JsonObject? args;
        try
        {
            args = string.IsNullOrWhiteSpace(argsJson) ? null : JsonNode.Parse(argsJson) as JsonObject;
            if (!string.IsNullOrWhiteSpace(argsJson) && args == null)
                return tr("Arguments must be a JSON object, e.g. {\"key\": \"value\"}");
        }
        catch (JsonException ex)
        {
            return string.Format(tr("Invalid JSON: {0}"), ex.Message);
        }

        string? eventName = string.IsNullOrWhiteSpace(evt) ? null : evt.Trim().ToUpperInvariant();

        try
        {
            JsonElement data = await session.SendDiagnosticAsync(command, args, eventName).ConfigureAwait(false);
            string reply = RpcDebugLog.Redact(data.ValueKind == JsonValueKind.Undefined ? "null" : data.GetRawText());
            logger.Info($"[Discord RPC tester] {command} → {reply}");
            return "OK: " + Truncate(reply);
        }
        catch (RpcException ex)
        {
            logger.Info($"[Discord RPC tester] {command} → ERROR {ex.Code} {ex.RpcMessage}");
            string hint = ex.Code == RpcErrorCodes.InvalidCommand ? " — " + tr("command does not exist") : string.Empty;
            return $"ERROR {ex.Code}: {ex.RpcMessage}{hint}";
        }
        catch (Exception ex)
        {
            return RpcErrorMapper.Describe(ex, RpcErrorContext.General, tr);
        }
    }

    private string Truncate(string text) =>
        text.Length <= MaxReplyLength ? text : text[..MaxReplyLength] + "… (" + tr("full reply in the log") + ")";
}
