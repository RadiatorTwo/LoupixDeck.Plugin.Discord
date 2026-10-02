using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Security;
using LoupixDeck.Plugin.Discord.Transport;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// Owns the whole connection: RPC client, authentication and event bus. Authenticates on its own
/// with the stored token after every (re)connect — the AUTHORIZE popup only ever appears through
/// <see cref="ConnectInteractiveAsync"/>, i.e. when the user presses "Connect" in the settings.
/// </summary>
internal sealed class DiscordSession : IDiscordRpc, IAsyncDisposable
{
    private readonly DiscordRpcClient _rpc;
    private readonly RpcEventBus _bus;
    private readonly DiscordAuthenticator _auth;
    private readonly ScopeRegistry _scopes;
    private readonly IPluginLogger _logger;
    private readonly Func<string, string> _tr;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _authenticated;

    public DiscordSession(Func<DiscordAppConfig> config, ISecretStore secrets, ScopeRegistry scopes,
        IPluginLogger logger, RpcDebugLog debug, Func<string, string> tr)
    {
        _logger = logger;
        _tr = tr;
        _scopes = scopes;
        _rpc = new DiscordRpcClient(() => config().ClientId, () => new DiscordIpcTransport(), logger, debug);
        _bus = new RpcEventBus(() => _authenticated,
            (cmd, args, evt) => _rpc.SendCommandAsync(cmd, args, evt), logger);
        _auth = new DiscordAuthenticator(_rpc, new OAuthTokenClient(), secrets, scopes, config, logger, tr);

        _rpc.PhaseChanged += OnPhaseChanged;
        _rpc.Ready += OnReady;
        _rpc.Disconnected += OnDisconnected;
        _rpc.EventReceived += _bus.Dispatch;
    }

    public DiscordConnectionState State { get; private set; } = new(DiscordConnectionStatus.Connecting);

    public bool IsReady => _authenticated;

    public string? CurrentUserId { get; private set; }

    private IReadOnlySet<string> _grantedScopes = new HashSet<string>();

    public bool HasScope(string scope) => _authenticated && _grantedScopes.Contains(scope);

    /// <summary>True when the granted scopes lack an optional one — "Connect" asks for it again.</summary>
    private bool MissingOptionalScopes(IEnumerable<string> granted)
    {
        HashSet<string> set = [.. granted];
        return _scopes.OptionalScopes.Any(s => !set.Contains(s));
    }

    public event Action<DiscordConnectionState>? StateChanged;

    public event Action? Authenticated;

    public event Action? ConnectionLost;

    public void Start() => _rpc.Start();

    /// <summary>Reconnects from scratch, e.g. after the client ID changed.</summary>
    public void Restart()
    {
        _authenticated = false;
        _rpc.Restart();
    }

    /// <summary>
    /// The "Connect with Discord" button: uses the stored token if it still works, otherwise runs
    /// AUTHORIZE (popup in Discord). Returns a translated status line for the settings page.
    /// </summary>
    public async Task<string> ConnectInteractiveAsync()
    {
        if (_rpc.Phase != RpcConnectionPhase.Ready)
        {
            // Nudge a waiting reconnect loop so a just-started Discord is picked up right away.
            _rpc.Restart();
            return State.Describe(_tr);
        }

        await _authLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            if (_authenticated && !MissingOptionalScopes(_grantedScopes))
                return State.Describe(_tr);

            // The stored token is enough unless optional permissions (a feature added since) are
            // missing; then the popup asks for them, and Discord may still refuse them.
            AuthOutcome? stored = null;
            if (!_authenticated)
            {
                stored = await _auth.AuthenticateStoredAsync(_shutdown.Token).ConfigureAwait(false);
                if (stored.Success && !MissingOptionalScopes(stored.GrantedScopes))
                {
                    await ApplyAsync(stored).ConfigureAwait(false);
                    return State.Describe(_tr);
                }
            }

            AuthOutcome authorized = await _auth.AuthorizeAsync(_shutdown.Token).ConfigureAwait(false);
            if (authorized.Success)
            {
                await ApplyAsync(authorized).ConfigureAwait(false);
                return State.Describe(_tr);
            }

            // Authorization failed (declined popup, …): keep whatever already works.
            if (stored?.Success == true)
                await ApplyAsync(stored).ConfigureAwait(false);
            else if (!_authenticated)
                await ApplyAsync(authorized).ConfigureAwait(false);

            return authorized.Failure ?? State.Describe(_tr);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>Signs out: revokes and deletes the tokens, then reconnects unauthenticated.</summary>
    public async Task SignOutAsync()
    {
        await _authLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await _auth.SignOutAsync(_shutdown.Token).ConfigureAwait(false);
            _authenticated = false;
            SetState(new DiscordConnectionState(DiscordConnectionStatus.NotAuthorized));
        }
        finally
        {
            _authLock.Release();
        }

        // Discord keeps an authenticated IPC session alive; only a new connection really drops it.
        _rpc.Restart();
    }

    public Task<JsonElement> CommandAsync(string cmd, JsonObject? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!_authenticated)
            throw new DiscordNotConnectedException(State.Describe(_tr));

        return _rpc.SendCommandAsync(cmd, args, timeout: timeout, ct: ct);
    }

    /// <summary>
    /// Diagnostics: sends an arbitrary command, with the top-level <c>evt</c> that SUBSCRIBE needs.
    /// Events subscribed this way are not tracked by the event bus; they only show up in the debug log.
    /// </summary>
    public Task<JsonElement> SendDiagnosticAsync(string cmd, JsonObject? args, string? evt)
    {
        if (!_authenticated)
            throw new DiscordNotConnectedException(State.Describe(_tr));

        return _rpc.SendCommandAsync(cmd, args, evt);
    }

    public IDisposable Subscribe(string evt, JsonObject? args, Action<JsonElement> handler) =>
        _bus.Subscribe(evt, args, handler);

    private void OnPhaseChanged(RpcConnectionPhase phase)
    {
        switch (phase)
        {
            case RpcConnectionPhase.NotConfigured:
                SetState(new DiscordConnectionState(DiscordConnectionStatus.NotConfigured));
                break;
            case RpcConnectionPhase.DiscordNotRunning:
                SetState(new DiscordConnectionState(DiscordConnectionStatus.DiscordNotRunning));
                break;
            case RpcConnectionPhase.Connecting:
                SetState(new DiscordConnectionState(DiscordConnectionStatus.Connecting));
                break;
            case RpcConnectionPhase.Rejected:
                SetState(new DiscordConnectionState(DiscordConnectionStatus.Error,
                    string.Format(_tr("Discord rejected the connection ({0}) — check the client ID"), _rpc.RejectReason)));
                break;
            case RpcConnectionPhase.Ready:
                // OnReady authenticates and sets the state from the outcome.
                break;
        }
    }

    private async void OnReady()
    {
        try
        {
            await _authLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (_authenticated) return;
                AuthOutcome outcome = await _auth.AuthenticateStoredAsync(_shutdown.Token).ConfigureAwait(false);
                await ApplyAsync(outcome).ConfigureAwait(false);
            }
            finally
            {
                _authLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("Discord authentication after connect failed", ex);
        }
    }

    private async Task ApplyAsync(AuthOutcome outcome)
    {
        if (!outcome.Success)
        {
            _authenticated = false;
            SetState(new DiscordConnectionState(
                outcome.NeedsAuthorization ? DiscordConnectionStatus.NotAuthorized : DiscordConnectionStatus.Error,
                outcome.Failure));
            return;
        }

        _grantedScopes = outcome.GrantedScopes.ToHashSet();
        _authenticated = true;
        CurrentUserId = outcome.UserId;
        SetState(new DiscordConnectionState(DiscordConnectionStatus.Connected, outcome.UserName));
        _logger.Info("Discord RPC authenticated.");

        await _bus.ResubscribeAllAsync().ConfigureAwait(false);

        foreach (Action handler in Authenticated?.GetInvocationList().Cast<Action>() ?? [])
        {
            try { handler(); }
            catch (Exception ex) { _logger.Error("Discord Authenticated handler failed", ex); }
        }
    }

    private void OnDisconnected()
    {
        _authenticated = false;
        foreach (Action handler in ConnectionLost?.GetInvocationList().Cast<Action>() ?? [])
        {
            try { handler(); }
            catch (Exception ex) { _logger.Error("Discord ConnectionLost handler failed", ex); }
        }
    }

    private void SetState(DiscordConnectionState state)
    {
        if (State == state) return;
        State = state;

        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { _logger.Error("Discord state handler failed", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _rpc.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
