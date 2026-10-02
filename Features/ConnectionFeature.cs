using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>A button that shows whether the plugin is connected to Discord.</summary>
internal sealed class ConnectionFeature(DiscordSession session) : IDiscordFeature
{
    public IReadOnlyCollection<string> RequiredScopes => [];

    public IEnumerable<IPluginCommand> Commands => [new ConnectionStatusCommand(session)];
}

internal sealed class ConnectionStatusCommand(DiscordSession session) : IDisplayCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "Discord.ConnectionStatus",
        DisplayName = "Discord: Connection Status",
        Group = "Discord",
        Icon = DiscordButtonLayouts.Connection,
        ButtonLayout = DiscordButtonLayouts.IconWithCaption(DiscordButtonLayouts.Connection, "Discord", tall: true),
        Description = "Shows whether the plugin is connected to Discord; press to reconnect"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public string GetText(CommandContext ctx)
    {
        DiscordConnectionState state = session.State;
        return state.Status switch
        {
            DiscordConnectionStatus.Connected => state.Detail ?? ctx.Host.Tr("Connected"),
            DiscordConnectionStatus.DiscordNotRunning => ctx.Host.Tr("Discord off"),
            DiscordConnectionStatus.Connecting => ctx.Host.Tr("Connecting…"),
            DiscordConnectionStatus.NotAuthorized => ctx.Host.Tr("Not authorized"),
            DiscordConnectionStatus.NotConfigured => ctx.Host.Tr("Not set up"),
            _ => ctx.Host.Tr("Error")
        };
    }

    public Task Execute(CommandContext ctx)
    {
        try
        {
            CommandFeedback.Show(ctx, session.State.Describe(ctx.Host.Tr));
            // Skip the backoff wait; an unauthorized connection is not fixed by reconnecting.
            if (session.State.Status is DiscordConnectionStatus.DiscordNotRunning or DiscordConnectionStatus.Error)
                session.Restart();
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error("Discord.ConnectionStatus failed", ex);
        }

        return Task.CompletedTask;
    }
}
