using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Transport;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>Where the connection to the Discord client currently stands (below authentication).</summary>
internal enum RpcConnectionPhase
{
    /// <summary>No client ID configured — nothing to connect with.</summary>
    NotConfigured,

    /// <summary>No IPC endpoint answered; retried with backoff.</summary>
    DiscordNotRunning,

    Connecting,

    /// <summary>Handshake done and READY received; commands can be sent.</summary>
    Ready,

    /// <summary>Discord closed the connection with an error (e.g. invalid client ID).</summary>
    Rejected
}

/// <summary>
/// The generic RPC layer on top of <see cref="IIpcTransport"/>: handshake, READY, PING/PONG,
/// automatic reconnect, and request/response matching by nonce. It does not authenticate —
/// <see cref="DiscordSession"/> does that once <see cref="Ready"/> fires.
/// </summary>
internal sealed class DiscordRpcClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RejectedRetry = TimeSpan.FromMinutes(1);

    private readonly Func<string?> _clientId;
    private readonly Func<IIpcTransport> _transportFactory;
    private readonly IPluginLogger _logger;
    private readonly RpcDebugLog _debug;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private IIpcTransport? _transport;
    private CancellationTokenSource? _connectionCts;
    private Task? _loop;

    public DiscordRpcClient(Func<string?> clientId, Func<IIpcTransport> transportFactory, IPluginLogger logger,
        RpcDebugLog debug)
    {
        _clientId = clientId;
        _transportFactory = transportFactory;
        _logger = logger;
        _debug = debug;
    }

    public RpcConnectionPhase Phase { get; private set; } = RpcConnectionPhase.Connecting;

    /// <summary>Why Discord rejected the last connection (only with <see cref="RpcConnectionPhase.Rejected"/>).</summary>
    public string? RejectReason { get; private set; }

    /// <summary>The <c>data</c> of the last READY event (RPC version, config, user).</summary>
    public JsonElement? ReadyData { get; private set; }

    public event Action<RpcConnectionPhase>? PhaseChanged;

    /// <summary>Raised on a worker thread after READY; commands may be sent from the handler.</summary>
    public event Action? Ready;

    /// <summary>Raised after an established connection was lost (Discord closed or crashed).</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Raised for every DISPATCH frame other than READY, on the reader thread. Handlers must not
    /// block — they would stall all further frames.
    /// </summary>
    public event Action<string, JsonElement>? EventReceived;

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Drops the current connection (if any) and connects again right away, e.g. after the client ID changed.</summary>
    public void Restart()
    {
        try { _connectionCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a wake-up is already pending */ }
    }

    private async Task RunAsync()
    {
        TimeSpan backoff = MinBackoff;
        CancellationToken shutdown = _shutdown.Token;

        while (!shutdown.IsCancellationRequested)
        {
            string? clientId = _clientId();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                SetPhase(RpcConnectionPhase.NotConfigured);
                await WaitAsync(Timeout.InfiniteTimeSpan, shutdown).ConfigureAwait(false);
                continue;
            }

            using CancellationTokenSource connectionCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            _connectionCts = connectionCts;
            IIpcTransport transport = _transportFactory();
            bool wasReady = false;
            TimeSpan delay = backoff;

            try
            {
                SetPhase(RpcConnectionPhase.Connecting);
                await transport.ConnectAsync(connectionCts.Token).ConfigureAwait(false);
                _transport = transport;

                JsonObject handshake = new() { ["v"] = 1, ["client_id"] = clientId };
                await SendFrameAsync(transport, IpcOpcode.Handshake, handshake.ToJsonString(), connectionCts.Token)
                    .ConfigureAwait(false);

                await ReadLoopAsync(transport, () =>
                {
                    wasReady = true;
                    backoff = MinBackoff;
                }, connectionCts.Token).ConfigureAwait(false);

                if (Phase == RpcConnectionPhase.Rejected)
                    delay = RejectedRetry;
            }
            catch (DiscordNotRunningException)
            {
                SetPhase(RpcConnectionPhase.DiscordNotRunning);
            }
            catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
            {
                // Restart() or shutdown — reconnect immediately unless shutting down.
                delay = TimeSpan.Zero;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                _logger.Info($"Discord IPC connection closed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error("Discord IPC connection failed", ex);
            }
            finally
            {
                _transport = null;
                _connectionCts = null;
                FailPending();
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            if (wasReady)
            {
                if (Phase == RpcConnectionPhase.Ready)
                    SetPhase(RpcConnectionPhase.Connecting);
                RaiseSafely(Disconnected, nameof(Disconnected));
            }

            if (delay > TimeSpan.Zero)
            {
                await WaitAsync(delay, shutdown).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ReadLoopAsync(IIpcTransport transport, Action onReady, CancellationToken ct)
    {
        while (true)
        {
            IpcFrame frame = await transport.ReadAsync(ct).ConfigureAwait(false);
            _debug.Incoming(frame.Opcode, frame.Json);

            switch (frame.Opcode)
            {
                case IpcOpcode.Ping:
                    await SendFrameAsync(transport, IpcOpcode.Pong, frame.Json, ct).ConfigureAwait(false);
                    break;

                case IpcOpcode.Close:
                    HandleClose(frame.Json);
                    return;

                case IpcOpcode.Frame:
                    HandleFrame(frame.Json, onReady);
                    break;
            }
        }
    }

    private void HandleClose(string json)
    {
        int code = 0;
        string message = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int parsed))
                code = parsed;
            if (doc.RootElement.TryGetProperty("message", out JsonElement m))
                message = m.GetString() ?? string.Empty;
        }
        catch (JsonException) { }

        _logger.Warn($"Discord closed the RPC connection: {code} {message}");

        // An invalid client ID will not get better by retrying every few seconds.
        if (code is RpcCloseCodes.InvalidClientId or RpcCloseCodes.InvalidOrigin or RpcCloseCodes.InvalidVersion)
        {
            RejectReason = $"{code} {message}".Trim();
            SetPhase(RpcConnectionPhase.Rejected);
        }
    }

    private void HandleFrame(string json, Action onReady)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.Warn($"Ignoring malformed Discord RPC frame: {ex.Message}");
            return;
        }

        string? cmd = GetString(root, "cmd");
        string? evt = GetString(root, "evt");
        string? nonce = GetString(root, "nonce");
        JsonElement data = root.TryGetProperty("data", out JsonElement d) ? d : default;

        // A reply to one of our commands — including SUBSCRIBE, whose reply carries evt as well.
        if (nonce != null && _pending.TryRemove(nonce, out TaskCompletionSource<JsonElement>? tcs))
        {
            if (evt == "ERROR")
                tcs.TrySetException(new RpcException(cmd ?? "?", GetInt(data, "code"), GetString(data, "message") ?? string.Empty));
            else
                tcs.TrySetResult(data);
            return;
        }

        if (cmd != "DISPATCH" || evt == null)
            return;

        if (evt == "READY")
        {
            ReadyData = data;
            RejectReason = null;
            onReady();
            SetPhase(RpcConnectionPhase.Ready);
            // Off the reader thread: the handler sends commands and must be able to read their replies.
            _ = Task.Run(() => RaiseSafely(Ready, nameof(Ready)));
            return;
        }

        if (evt == "ERROR")
            _logger.Warn($"Discord RPC error event: {GetInt(data, "code")} {GetString(data, "message")}");

        Action<string, JsonElement>? handlers = EventReceived;
        if (handlers == null) return;

        foreach (Action<string, JsonElement> handler in handlers.GetInvocationList().Cast<Action<string, JsonElement>>())
        {
            try { handler(evt, data); }
            catch (Exception ex) { _logger.Error($"Discord event handler for {evt} failed", ex); }
        }
    }

    /// <summary>
    /// Sends <paramref name="cmd"/> with a fresh nonce and returns the reply's <c>data</c>.
    /// Throws <see cref="RpcException"/> on an ERROR reply, <see cref="RpcTimeoutException"/> when
    /// Discord does not answer in time and <see cref="DiscordNotConnectedException"/> without a connection.
    /// </summary>
    /// <param name="args">Command arguments; cloned, so the caller may reuse the object.</param>
    /// <param name="evt">Top-level <c>evt</c> field — only SUBSCRIBE/UNSUBSCRIBE use it.</param>
    public async Task<JsonElement> SendCommandAsync(string cmd, JsonObject? args = null, string? evt = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        IIpcTransport transport = Phase == RpcConnectionPhase.Ready ? _transport ?? throw NotConnected()
            : throw NotConnected();

        string nonce = Guid.NewGuid().ToString("N");
        JsonObject payload = new()
        {
            ["cmd"] = cmd,
            ["args"] = args?.DeepClone() ?? new JsonObject(),
            ["nonce"] = nonce
        };
        if (evt != null)
            payload["evt"] = evt;

        TaskCompletionSource<JsonElement> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;

        TimeSpan wait = timeout ?? DefaultTimeout;
        try
        {
            await SendFrameAsync(transport, IpcOpcode.Frame, payload.ToJsonString(), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RpcTimeoutException(cmd, wait);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw NotConnected();
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task SendFrameAsync(IIpcTransport transport, IpcOpcode opcode, string json, CancellationToken ct)
    {
        _debug.Outgoing(opcode, json);
        await transport.WriteAsync(new IpcFrame(opcode, json), ct).ConfigureAwait(false);
    }

    private void FailPending()
    {
        foreach (string nonce in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(nonce, out TaskCompletionSource<JsonElement>? tcs))
                tcs.TrySetException(NotConnected());
        }
    }

    private static DiscordNotConnectedException NotConnected() => new(RpcErrorMapper.NotConnected);

    private void SetPhase(RpcConnectionPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;

        Action<RpcConnectionPhase>? handlers = PhaseChanged;
        if (handlers == null) return;
        try { handlers(phase); }
        catch (Exception ex) { _logger.Error("Discord phase handler failed", ex); }
    }

    private void RaiseSafely(Action? handlers, string name)
    {
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception ex) { _logger.Error($"Discord {name} handler failed", ex); }
        }
    }

    internal static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : 0;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Wake();
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* shutting down anyway */ }
        }

        _shutdown.Dispose();
    }
}
