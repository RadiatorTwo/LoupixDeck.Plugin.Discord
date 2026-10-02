using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Security;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>Result of an authentication attempt; <see cref="Failure"/> is already translated.</summary>
internal sealed record AuthOutcome(bool Success, string? UserId, string? UserName, string? Failure,
    bool NeedsAuthorization)
{
    public static AuthOutcome Ok(string? userId, string? userName) => new(true, userId, userName, null, false);

    public static AuthOutcome NeedsAuth(string message) => new(false, null, null, message, true);

    public static AuthOutcome Failed(string message) => new(false, null, null, message, false);
}

/// <summary>
/// The OAuth flow over RPC (RPC docs, "Authenticating"): AUTHORIZE (popup in Discord) → code →
/// token exchange → AUTHENTICATE. Tokens are kept in the <see cref="ISecretStore"/> and refreshed
/// with the refresh token (RFC 6749 §6), so the popup is only needed once — and again when the
/// requested scopes change.
/// </summary>
internal sealed class DiscordAuthenticator(
    DiscordRpcClient rpc,
    OAuthTokenClient oauth,
    ISecretStore secrets,
    ScopeRegistry scopes,
    Func<DiscordAppConfig> config,
    IPluginLogger logger,
    Func<string, string> tr)
{
    private const string TokenSecretName = "oauth_tokens";

    /// <summary>Refresh this long before the access token expires.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>AUTHORIZE waits for the user to answer the popup.</summary>
    private static readonly TimeSpan AuthorizeTimeout = TimeSpan.FromMinutes(3);

    public bool HasStoredTokens => LoadTokens() != null;

    /// <summary>Authenticates with the stored token without ever showing the popup.</summary>
    public async Task<AuthOutcome> AuthenticateStoredAsync(CancellationToken ct)
    {
        DiscordAppConfig app = config();
        TokenSet? tokens = LoadTokens();

        // A token issued to a different application is useless for this one.
        if (tokens == null || tokens.ClientId != app.ClientId)
            return AuthOutcome.NeedsAuth(tr(RpcErrorMapper.NotAuthorized));
        if (!scopes.IsCoveredBy(tokens.Scopes))
            return AuthOutcome.NeedsAuth(tr(RpcErrorMapper.ScopesChanged));

        try
        {
            if (tokens.ExpiresAt - RefreshMargin <= DateTimeOffset.UtcNow)
                tokens = await RefreshAsync(app, tokens, ct).ConfigureAwait(false);

            try
            {
                return await AuthenticateAsync(tokens, ct).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.Code == RpcErrorCodes.InvalidToken && tokens.RefreshToken != null)
            {
                // Revoked or expired earlier than announced — one refresh, then give up.
                tokens = await RefreshAsync(app, tokens, ct).ConfigureAwait(false);
                return await AuthenticateAsync(tokens, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsTokenDead(ex))
        {
            logger.Info($"Stored Discord token is no longer valid ({ex.Message}); authorization needed.");
            ClearTokens();
            return AuthOutcome.NeedsAuth(RpcErrorMapper.Describe(ex, RpcErrorContext.Authenticate, tr));
        }
        catch (Exception ex)
        {
            // Network trouble or Discord hiccup: keep the tokens, the next reconnect tries again.
            logger.Warn($"Discord authentication failed: {ex.Message}");
            return AuthOutcome.Failed(RpcErrorMapper.Describe(ex, RpcErrorContext.Authenticate, tr));
        }
    }

    /// <summary>Runs the full AUTHORIZE flow — this shows the popup in Discord.</summary>
    public async Task<AuthOutcome> AuthorizeAsync(CancellationToken ct)
    {
        DiscordAppConfig app = config();
        if (!app.IsComplete)
            return AuthOutcome.NeedsAuth(tr(RpcErrorMapper.MissingConfig));

        try
        {
            JsonObject args = new()
            {
                ["client_id"] = app.ClientId,
                ["scopes"] = new JsonArray(scopes.Scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
            };

            JsonElement data = await rpc.SendCommandAsync("AUTHORIZE", args, timeout: AuthorizeTimeout, ct: ct)
                .ConfigureAwait(false);
            string code = DiscordRpcClient.GetString(data, "code")
                          ?? throw new RpcException("AUTHORIZE", RpcErrorCodes.OAuth2Error, "No code in reply");

            TokenSet tokens = await oauth.ExchangeCodeAsync(app, code, ct).ConfigureAwait(false);
            if (tokens.Scopes.Count == 0)
                tokens = tokens with { Scopes = scopes.Scopes.ToList() };
            SaveTokens(tokens);

            return await AuthenticateAsync(tokens, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warn($"Discord authorization failed: {ex.Message}");
            return AuthOutcome.NeedsAuth(RpcErrorMapper.Describe(ex, RpcErrorContext.Authorize, tr));
        }
    }

    /// <summary>Forgets the tokens locally and revokes them at Discord (best effort).</summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        TokenSet? tokens = LoadTokens();
        ClearTokens();
        if (tokens == null) return;

        DiscordAppConfig app = config();
        if (!app.IsComplete) return;

        try
        {
            await oauth.RevokeAsync(app, tokens.RefreshToken ?? tokens.AccessToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Info($"Revoking the Discord token failed: {ex.Message}");
        }
    }

    private async Task<AuthOutcome> AuthenticateAsync(TokenSet tokens, CancellationToken ct)
    {
        JsonObject args = new() { ["access_token"] = tokens.AccessToken };
        JsonElement data = await rpc.SendCommandAsync("AUTHENTICATE", args, ct: ct).ConfigureAwait(false);

        // The reply lists what was actually granted; keep it so a later scope check is accurate.
        if (data.TryGetProperty("scopes", out JsonElement granted) && granted.ValueKind == JsonValueKind.Array)
        {
            List<string> grantedScopes = granted.EnumerateArray()
                .Select(s => s.GetString())
                .OfType<string>()
                .ToList();
            if (grantedScopes.Count > 0 && !grantedScopes.SequenceEqual(tokens.Scopes))
                SaveTokens(tokens with { Scopes = grantedScopes });
        }

        string? userId = null;
        string? userName = null;
        if (data.TryGetProperty("user", out JsonElement u))
        {
            userId = DiscordRpcClient.GetString(u, "id");
            userName = DiscordRpcClient.GetString(u, "global_name") ?? DiscordRpcClient.GetString(u, "username");
        }

        return AuthOutcome.Ok(userId, userName);
    }

    private async Task<TokenSet> RefreshAsync(DiscordAppConfig app, TokenSet tokens, CancellationToken ct)
    {
        if (tokens.RefreshToken == null || string.IsNullOrWhiteSpace(app.ClientSecret))
            throw new OAuthException(0, "invalid_grant");

        TokenSet refreshed = await oauth.RefreshAsync(app, tokens.RefreshToken, ct).ConfigureAwait(false);
        if (refreshed.Scopes.Count == 0)
            refreshed = refreshed with { Scopes = tokens.Scopes };
        SaveTokens(refreshed);
        return refreshed;
    }

    private static bool IsTokenDead(Exception ex) =>
        ex is OAuthException { Error: "invalid_grant" }
        || (ex is RpcException rpcEx && rpcEx.Code == RpcErrorCodes.InvalidToken);

    private TokenSet? LoadTokens()
    {
        string? json = secrets.Get(TokenSecretName);
        if (string.IsNullOrEmpty(json)) return null;

        try { return JsonSerializer.Deserialize<TokenSet>(json); }
        catch (JsonException) { return null; }
    }

    private void SaveTokens(TokenSet tokens) => secrets.Set(TokenSecretName, JsonSerializer.Serialize(tokens));

    public void ClearTokens() => secrets.Set(TokenSecretName, null);
}
