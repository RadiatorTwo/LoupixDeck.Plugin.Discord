namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// The Discord application the plugin talks as — either the user's own application or one whose
/// tester list they are on. All three values come from the plugin settings.
/// </summary>
internal sealed record DiscordAppConfig(string ClientId, string ClientSecret, string RedirectUri)
{
    public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);

    public bool IsComplete => HasClientId && !string.IsNullOrWhiteSpace(ClientSecret)
                                          && !string.IsNullOrWhiteSpace(RedirectUri);
}
