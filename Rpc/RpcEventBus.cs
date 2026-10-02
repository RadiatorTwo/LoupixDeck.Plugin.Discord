using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// Generic event subscriptions on top of SUBSCRIBE/UNSUBSCRIBE (RPC docs, "SUBSCRIBE").
/// </summary>
/// <remarks>
/// <para>
/// Several handlers may subscribe to the same event with the same arguments; Discord sees one
/// SUBSCRIBE for them and one UNSUBSCRIBE once the last handler is gone. Registrations survive
/// a lost connection: <see cref="ResubscribeAllAsync"/> re-sends them after every successful
/// authentication, so callers subscribe once and never think about reconnects.
/// </para>
/// <para>
/// Incoming events are routed by name only. Discord's event payloads do not reliably echo the
/// subscription arguments (e.g. SPEAKING_START only documents <c>user_id</c>), so a handler that
/// subscribes the same event with different arguments must filter itself.
/// </para>
/// </remarks>
internal sealed class RpcEventBus(Func<bool> canSend, Func<string, JsonObject?, string, Task> send, IPluginLogger logger)
{
    private readonly Lock _gate = new();
    private readonly List<Registration> _registrations = [];

    /// <summary>
    /// Registers <paramref name="handler"/> for <paramref name="evt"/>. Dispose the result to unsubscribe.
    /// The handler runs on the RPC reader thread and must return quickly.
    /// </summary>
    public IDisposable Subscribe(string evt, JsonObject? args, Action<JsonElement> handler)
    {
        Registration registration = new(this, evt, args?.DeepClone().AsObject(), handler);
        bool first;
        lock (_gate)
        {
            first = _registrations.All(r => r.Key != registration.Key);
            _registrations.Add(registration);
        }

        if (first && canSend())
            _ = SendSafelyAsync("SUBSCRIBE", registration);

        return registration;
    }

    private void Remove(Registration registration)
    {
        bool last;
        lock (_gate)
        {
            if (!_registrations.Remove(registration)) return;
            last = _registrations.All(r => r.Key != registration.Key);
        }

        if (last && canSend())
            _ = SendSafelyAsync("UNSUBSCRIBE", registration);
    }

    /// <summary>Sends SUBSCRIBE for every distinct registration — after (re)authentication.</summary>
    public async Task ResubscribeAllAsync()
    {
        List<Registration> distinct;
        lock (_gate)
            distinct = _registrations.DistinctBy(r => r.Key).ToList();

        foreach (Registration registration in distinct)
            await SendSafelyAsync("SUBSCRIBE", registration).ConfigureAwait(false);
    }

    /// <summary>Hands an incoming DISPATCH event to its handlers.</summary>
    public void Dispatch(string evt, JsonElement data)
    {
        List<Registration> targets;
        lock (_gate)
            targets = _registrations.Where(r => r.Event == evt).ToList();

        foreach (Registration registration in targets)
        {
            try { registration.Handler(data); }
            catch (Exception ex) { logger.Error($"Discord handler for {evt} failed", ex); }
        }
    }

    private async Task SendSafelyAsync(string cmd, Registration registration)
    {
        try
        {
            await send(cmd, registration.Args, registration.Event).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            // Typically 4006: the granted scopes do not cover this event.
            logger.Warn($"{cmd} {registration.Event} rejected by Discord: {ex.Code} {ex.RpcMessage}");
        }
        catch (Exception ex)
        {
            logger.Warn($"{cmd} {registration.Event} failed: {ex.Message}");
        }
    }

    private sealed class Registration(RpcEventBus owner, string evt, JsonObject? args, Action<JsonElement> handler)
        : IDisposable
    {
        public string Event { get; } = evt;
        public JsonObject? Args { get; } = args;
        public Action<JsonElement> Handler { get; } = handler;
        public string Key { get; } = evt + "|" + (args?.ToJsonString() ?? "{}");

        public void Dispose() => owner.Remove(this);
    }
}
