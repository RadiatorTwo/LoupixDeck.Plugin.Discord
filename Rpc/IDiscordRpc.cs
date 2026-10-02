using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// Everything a feature may use: generic commands and event subscriptions on an authenticated
/// connection. Features never see the transport, the tokens or the authorization flow.
/// </summary>
internal interface IDiscordRpc
{
    /// <summary>True while connected and authenticated — commands can be sent.</summary>
    bool IsReady { get; }

    /// <summary>ID of the signed-in Discord user, from AUTHENTICATE.</summary>
    string? CurrentUserId { get; }

    /// <summary>
    /// Sends any RPC command (documented or not) and returns the reply's <c>data</c>.
    /// Throws <see cref="RpcException"/>, <see cref="RpcTimeoutException"/> or
    /// <see cref="DiscordNotConnectedException"/>; <see cref="RpcErrorMapper"/> turns them into text.
    /// </summary>
    Task<JsonElement> CommandAsync(string cmd, JsonObject? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to an RPC event; survives reconnects. Dispose the result to unsubscribe.
    /// The handler runs on the reader thread and must not block.
    /// </summary>
    IDisposable Subscribe(string evt, JsonObject? args, Action<JsonElement> handler);

    /// <summary>Raised after every successful (re)authentication, on a worker thread.</summary>
    event Action? Authenticated;

    /// <summary>Raised when an authenticated connection is lost.</summary>
    event Action? ConnectionLost;
}
