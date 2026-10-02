using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// One group of Discord actions. A feature talks to Discord only through
/// <see cref="Rpc.IDiscordRpc"/> (commands + event subscriptions) and declares the OAuth scopes
/// it needs; transport and authentication are not its concern.
/// </summary>
/// <remarks>
/// Adding a feature: implement this interface, subscribe to events in the constructor (the
/// subscriptions survive reconnects), and add one line to <c>DiscordPlugin.CreateFeatures</c>.
/// New scopes are picked up automatically; users are asked to connect again once.
/// </remarks>
internal interface IDiscordFeature
{
    /// <summary>OAuth2 scopes on top of the base set (<c>rpc</c>, <c>identify</c>).</summary>
    IReadOnlyCollection<string> RequiredScopes { get; }

    IEnumerable<IPluginCommand> Commands { get; }

    /// <summary>
    /// Entries for the plugin's branch of the command menu (channel pickers, user pickers, …).
    /// Must answer from cached data; the host gives the whole menu 5 seconds.
    /// </summary>
    IEnumerable<MenuNode> GetMenuNodes(ButtonTargets target) => [];

    /// <summary>
    /// Called before <see cref="GetMenuNodes"/> each time the menu is built, to refresh cached
    /// lists from Discord. Bounded by <paramref name="ct"/>; on timeout the old cache is shown.
    /// </summary>
    Task RefreshForMenuAsync(CancellationToken ct) => Task.CompletedTask;
}
