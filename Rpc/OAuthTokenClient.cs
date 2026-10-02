using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>The OAuth2 tokens of one authorization, as persisted in the secret store.</summary>
internal sealed record TokenSet
{
    [JsonPropertyName("client_id")] public required string ClientId { get; init; }
    [JsonPropertyName("access_token")] public required string AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("scopes")] public IReadOnlyList<string> Scopes { get; init; } = [];
}

/// <summary>The token endpoint answered with an OAuth error (RFC 6749 §5.2) or a non-success status.</summary>
internal sealed class OAuthException(int statusCode, string? error, string? description = null)
    : Exception($"Discord token endpoint returned {statusCode} {error} {description}".TrimEnd())
{
    public int StatusCode { get; } = statusCode;

    /// <summary><c>invalid_grant</c>, <c>invalid_client</c>, … — never contains a secret.</summary>
    public string? Error { get; } = error;

    /// <summary>The server's <c>error_description</c>, if any.</summary>
    public string? Description { get; } = description;
}

/// <summary>
/// Exchanges an authorization code for tokens and refreshes them
/// (<c>POST https://discord.com/api/oauth2/token</c>, form-urlencoded; RFC 6749 §4.1.3 and §6).
/// Nothing sent or received here is ever logged.
/// </summary>
internal sealed class OAuthTokenClient
{
    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public Task<TokenSet> ExchangeCodeAsync(DiscordAppConfig app, string code, CancellationToken ct) =>
        RequestAsync(app, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            // Must match a redirect URI registered for the application in the Developer Portal.
            ["redirect_uri"] = app.RedirectUri
        }, previousRefreshToken: null, ct);

    public Task<TokenSet> RefreshAsync(DiscordAppConfig app, string refreshToken, CancellationToken ct) =>
        RequestAsync(app, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        }, refreshToken, ct);

    /// <summary>Revokes a token (<c>/oauth2/token/revoke</c>); revoking one invalidates all tokens of the grant.</summary>
    public async Task RevokeAsync(DiscordAppConfig app, string token, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, TokenEndpoint + "/revoke");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = app.ClientId,
            ["client_secret"] = app.ClientSecret
        });

        using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OAuthException((int)response.StatusCode, null);
    }

    private static async Task<TokenSet> RequestAsync(DiscordAppConfig app, Dictionary<string, string> form,
        string? previousRefreshToken, CancellationToken ct)
    {
        // Client credentials in the body, one of the two methods Discord documents.
        form["client_id"] = app.ClientId;
        form["client_secret"] = app.ClientSecret;

        using HttpRequestMessage request = new(HttpMethod.Post, TokenEndpoint);
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new OAuthException((int)response.StatusCode, null);
        }

        if (!response.IsSuccessStatusCode)
            throw new OAuthException((int)response.StatusCode, DiscordRpcClient.GetString(root, "error"),
                DiscordRpcClient.GetString(root, "error_description"));

        string accessToken = DiscordRpcClient.GetString(root, "access_token")
                             ?? throw new OAuthException((int)response.StatusCode, "missing_access_token");
        int expiresIn = DiscordRpcClient.GetInt(root, "expires_in");
        string scope = DiscordRpcClient.GetString(root, "scope") ?? string.Empty;

        return new TokenSet
        {
            ClientId = app.ClientId,
            AccessToken = accessToken,
            // RFC 6749 §6: the server MAY issue a new refresh token; otherwise the old one stays valid.
            RefreshToken = DiscordRpcClient.GetString(root, "refresh_token") ?? previousRefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 3600),
            Scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        };
    }
}
