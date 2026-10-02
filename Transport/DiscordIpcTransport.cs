using System.IO.Pipes;
using System.Net.Sockets;

namespace LoupixDeck.Plugin.Discord.Transport;

/// <summary>
/// Connects to the Discord client's local IPC endpoint (RPC docs, "RPC over IPC"):
/// <list type="bullet">
///   <item>Windows: named pipe <c>\\?\pipe\discord-ipc-{n}</c></item>
///   <item>Linux/macOS: Unix socket <c>discord-ipc-{n}</c> in the first existing directory of
///   <c>$XDG_RUNTIME_DIR</c>, <c>$TMPDIR</c>, <c>$TMP</c>, <c>$TEMP</c>, else <c>/tmp</c></item>
/// </list>
/// <c>n</c> is tried from 0 to 9; several Discord instances (stable, PTB, Canary) take consecutive numbers.
/// </summary>
internal sealed class DiscordIpcTransport : IIpcTransport
{
    private const int MaxPipeIndex = 9;
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Stream? _stream;
    private Socket? _socket;

    public bool IsConnected => _stream != null;

    /// <summary>The endpoint of the current connection, for diagnostics.</summary>
    public string? Endpoint { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await CloseAsync().ConfigureAwait(false);

        for (int n = 0; n <= MaxPipeIndex; n++)
        {
            ct.ThrowIfCancellationRequested();

            if (OperatingSystem.IsWindows())
            {
                if (await TryConnectPipeAsync(n, ct).ConfigureAwait(false))
                    return;
            }
            else if (await TryConnectSocketAsync(n, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new DiscordNotRunningException();
    }

    private async Task<bool> TryConnectPipeAsync(int n, CancellationToken ct)
    {
        string name = $"discord-ipc-{n}";
        NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(PipeConnectTimeout, ct).ConfigureAwait(false);
            _stream = pipe;
            Endpoint = $@"\\?\pipe\{name}";
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> TryConnectSocketAsync(int n, CancellationToken ct)
    {
        string path = Path.Combine(ResolveSocketDirectory(), $"discord-ipc-{n}");
        if (!File.Exists(path))
            return false;

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);
            Endpoint = path;
            return true;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return false;
        }
    }

    /// <summary>
    /// The directory Discord creates its socket in: the first of these environment variables that
    /// points to an existing directory, else <c>/tmp</c> (resolution order as documented).
    /// </summary>
    internal static string ResolveSocketDirectory()
    {
        foreach (string variable in (string[])["XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP"])
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value) && Directory.Exists(value))
                return value;
        }

        return "/tmp";
    }

    public async Task WriteAsync(IpcFrame frame, CancellationToken ct)
    {
        Stream stream = _stream ?? throw new IOException("Discord IPC is not connected.");
        byte[] bytes = frame.Encode();

        // Commands are sent from several threads (buttons, event handlers, PONG replies); a frame
        // must never be interleaved with another one.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IpcFrame> ReadAsync(CancellationToken ct)
    {
        Stream stream = _stream ?? throw new IOException("Discord IPC is not connected.");
        return IpcFrame.ReadAsync(stream, ct);
    }

    private async Task CloseAsync()
    {
        Stream? stream = _stream;
        Socket? socket = _socket;
        _stream = null;
        _socket = null;
        Endpoint = null;

        if (stream != null)
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch { /* already broken */ }
        }

        socket?.Dispose();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
