namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>What the failed call was trying to do — the same code means different things per context.</summary>
internal enum RpcErrorContext
{
    General,
    Authorize,
    Authenticate,
    VoiceSettingsWrite,
    SelectChannel
}

/// <summary>
/// Turns RPC, OAuth and connection failures into one short English sentence for the user
/// (translated by the caller through <c>host.Tr</c>).
/// </summary>
internal static class RpcErrorMapper
{
    public const string NotConnected = "Discord is not connected";
    public const string NotRunning = "Discord is not running";
    public const string NotAuthorized = "Not authorized — connect in the plugin settings";
    public const string ScopesChanged = "New permissions needed — connect again in the plugin settings";
    public const string MissingConfig = "Enter client ID, client secret and redirect URI in the plugin settings";
    public const string NotInVoiceChannel = "Not in a voice channel";
    public const string AlreadyInVoiceChannel = "Already in a voice channel";

    // TODO(rpc-errors): Discord documents no specific code for "user declined the popup" or
    // "user is not on the tester list". Both are expected to arrive as an ERROR reply to AUTHORIZE
    // (probably 5000 "OAuth2 error" or 4000). Capture the real code/message with the debug log
    // enabled and split this message once they are known.
    public const string AuthorizeFailed =
        "Authorization failed — popup declined, or your Discord account is not on the application's tester list";

    // TODO(voice-lock): The RPC docs state that only one app may change voice settings at a time
    // (the first one locks them until it disconnects) but name no error code. Until the real reply
    // is known (debug log, with e.g. the Elgato plugin connected), every unexpected error on a voice
    // settings write is reported as a possible lock.
    public const string VoiceSettingsLocked =
        "Voice settings are locked by another app (e.g. Stream Deck). Close or disconnect it and try again";

    public static string Describe(Exception ex, RpcErrorContext context, Func<string, string> tr) => ex switch
    {
        // Carries the current connection state ("Discord is not running", "Not authorized …").
        DiscordNotConnectedException notConnected => tr(notConnected.Message),
        RpcTimeoutException when context == RpcErrorContext.Authorize =>
            tr("The authorization popup in Discord was not confirmed in time"),
        RpcTimeoutException => tr("Discord did not answer in time"),
        OAuthException oauth => DescribeOAuth(oauth, tr),
        HttpRequestException => tr("Could not reach discord.com"),
        RpcException rpc => DescribeRpc(rpc, context, tr),
        _ => tr("Unexpected error — see the log")
    };

    private static string DescribeRpc(RpcException ex, RpcErrorContext context, Func<string, string> tr)
    {
        switch (ex.Code)
        {
            case RpcErrorCodes.InvalidPermissions:
                return context == RpcErrorContext.Authorize ? tr(AuthorizeFailed) : tr(ScopesChanged);
            case RpcErrorCodes.InvalidToken:
                return tr("Discord login expired — connect again in the plugin settings");
            case RpcErrorCodes.InvalidClientId:
                return tr("Discord does not accept the client ID");
            case RpcErrorCodes.SelectVoiceForceRequired:
                return tr(AlreadyInVoiceChannel);
            case RpcErrorCodes.SelectChannelTimedOut:
                return tr("Discord did not switch the channel in time");
            case RpcErrorCodes.InvalidChannel:
                return tr("Channel not found or not accessible");
            case RpcErrorCodes.InvalidGuild:
                return tr("Server not found or not accessible");
            case RpcErrorCodes.InvalidUser:
                return tr("User not found");
        }

        return context switch
        {
            RpcErrorContext.Authorize => tr(AuthorizeFailed),
            RpcErrorContext.VoiceSettingsWrite => tr(VoiceSettingsLocked),
            // Discord's own message is English and specific; better than a generic sentence.
            _ => string.Format(tr("Discord error {0}: {1}"), ex.Code, ex.RpcMessage)
        };
    }

    private static string DescribeOAuth(OAuthException ex, Func<string, string> tr)
    {
        // The RFC 6749 error code does not distinguish a wrong redirect URI from other bad
        // requests; the description names the offending parameter.
        if (ex.Description?.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase) == true)
            return tr("Redirect URI does not match the one in the Developer Portal");

        return ex.Error switch
        {
            "invalid_client" => tr("Client ID or client secret is wrong"),
            "invalid_grant" => tr("Discord login expired — connect again in the plugin settings"),
            _ => string.Format(tr("Discord token request failed ({0})"), ex.Error ?? ex.StatusCode.ToString())
        };
    }
}
