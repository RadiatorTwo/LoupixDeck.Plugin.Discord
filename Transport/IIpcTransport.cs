namespace LoupixDeck.Plugin.Discord.Transport;

/// <summary>
/// A raw, frame-level connection to the local Discord client. Knows nothing about RPC commands,
/// nonces or authentication — that is the RPC client's job.
/// </summary>
internal interface IIpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// Connects to the first Discord IPC endpoint that answers.
    /// Throws <see cref="DiscordNotRunningException"/> when none does.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    Task WriteAsync(IpcFrame frame, CancellationToken ct);

    /// <summary>Reads the next frame; throws <see cref="EndOfStreamException"/> or <see cref="IOException"/> when Discord goes away.</summary>
    Task<IpcFrame> ReadAsync(CancellationToken ct);
}

/// <summary>No <c>discord-ipc-{n}</c> endpoint could be opened — Discord is not running.</summary>
internal sealed class DiscordNotRunningException() : Exception("No Discord IPC endpoint found (is Discord running?).");
