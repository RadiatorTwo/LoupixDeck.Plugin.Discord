using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>Opens a text channel in the Discord app (SELECT_TEXT_CHANNEL).</summary>
internal sealed class TextChannelFeature(IDiscordRpc rpc, GuildDirectory guilds) : IDiscordFeature
{
    public IReadOnlyCollection<string> RequiredScopes => [];

    public IEnumerable<IPluginCommand> Commands { get; } = [new OpenTextChannelCommand(rpc)];

    public Task RefreshForMenuAsync(CancellationToken ct) => guilds.RefreshAsync(ct);

    public IEnumerable<MenuNode> GetMenuNodes(ButtonTargets target)
    {
        List<MenuNode> servers = guilds.BuildChannelMenu(c => c.IsText, OpenTextChannelCommand.Name, "channelId");
        return servers.Count == 0 ? [] : [new MenuNode { Name = "Open text channel", Children = servers }];
    }
}

internal sealed class OpenTextChannelCommand(IDiscordRpc rpc) : IPluginCommand
{
    public const string Name = "Discord.OpenTextChannel";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Open Text Channel",
        Group = "Discord",
        Icon = DiscordButtonLayouts.TextChannel,
        ButtonLayout = DiscordButtonLayouts.IconWithCaption(DiscordButtonLayouts.TextChannel, "Text Channel"),
        Description = "Switches the Discord app to a text channel",
        HiddenFromMenu = true,
        ParameterTemplate = "({channelId})",
        Parameters = [new CommandParameter("channelId", typeof(string))]
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        string? channelId = ctx.Parameters.Length > 0 ? ctx.Parameters[0].Trim() : null;
        if (string.IsNullOrEmpty(channelId)) return;

        try
        {
            await rpc.CommandAsync("SELECT_TEXT_CHANNEL", new JsonObject { ["channel_id"] = channelId },
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex, RpcErrorContext.SelectChannel);
        }
    }
}
