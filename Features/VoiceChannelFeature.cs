using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// Joining and leaving voice channels (SELECT_VOICE_CHANNEL), plus displays for the current
/// channel (GET_SELECTED_VOICE_CHANNEL / VOICE_CHANNEL_SELECT) and who is speaking
/// (SPEAKING_START / SPEAKING_STOP).
/// </summary>
internal sealed class VoiceChannelFeature : IDiscordFeature, IDisposable
{
    private readonly VoiceStateTracker _tracker;
    private readonly GuildDirectory _guilds;
    private readonly IPluginHost _host;

    public VoiceChannelFeature(IDiscordRpc rpc, VoiceStateTracker tracker, GuildDirectory guilds, IPluginHost host)
    {
        _tracker = tracker;
        _guilds = guilds;
        _host = host;
        Commands =
        [
            new JoinVoiceChannelCommand(rpc, guilds),
            new LeaveVoiceChannelCommand(rpc, tracker),
            new CurrentVoiceChannelCommand(rpc, tracker),
            new SpeakingCommand(rpc, tracker)
        ];

        tracker.Changed += OnVoiceChanged;
    }

    public IReadOnlyCollection<string> RequiredScopes => VoiceStateTracker.Scopes;

    public IEnumerable<IPluginCommand> Commands { get; }

    public IEnumerable<MenuNode> GetMenuNodes(ButtonTargets target)
    {
        List<MenuNode> servers = _guilds.BuildChannelMenu(c => c.IsVoice, JoinVoiceChannelCommand.Name, "channelId");
        return servers.Count == 0 ? [] : [new MenuNode { Name = "Join voice channel", Children = servers }];
    }

    private void OnVoiceChanged(VoiceChange change)
    {
        if (change.HasFlag(VoiceChange.Channel))
            _host.RequestButtonRefresh(CurrentVoiceChannelCommand.Name);
        if (change.HasFlag(VoiceChange.Speaking))
            _host.RequestButtonRefresh(SpeakingCommand.Name);
    }

    public void Dispose() => _tracker.Changed -= OnVoiceChanged;
}

internal sealed class JoinVoiceChannelCommand(IDiscordRpc rpc, GuildDirectory guilds) : IPluginCommand
{
    public const string Name = "Discord.JoinVoiceChannel";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Join Voice Channel",
        Group = "Discord",
        Icon = "\U000F05CB",
        Description = "Joins a voice channel. With 'force', moves you even if you are already in another channel",
        HiddenFromMenu = true,
        ParameterTemplate = "({channelId},{force})",
        Parameters =
        [
            new CommandParameter("channelId", typeof(string)),
            new CommandParameter("force", typeof(bool)) { DefaultValue = "False" }
        ]
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        string? channelId = ctx.Parameters.Length > 0 ? ctx.Parameters[0].Trim() : null;
        if (string.IsNullOrEmpty(channelId)) return;

        // The docs allow "force" only when the user approved being moved — configuring it on
        // the button is that approval. Without it, Discord answers 5003 when already in a channel.
        bool force = ctx.Parameters.Length > 1 && bool.TryParse(ctx.Parameters[1], out bool f) && f;

        try
        {
            JsonObject args = new() { ["channel_id"] = channelId };
            if (force)
                args["force"] = true;

            await rpc.CommandAsync("SELECT_VOICE_CHANNEL", args, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            CommandFeedback.Show(ctx, guilds.FindChannelName(channelId) ?? ctx.Host.Tr("Joined"));
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex, RpcErrorContext.SelectChannel);
        }
    }
}

internal sealed class LeaveVoiceChannelCommand(IDiscordRpc rpc, VoiceStateTracker tracker) : IPluginCommand
{
    public const string Name = "Discord.LeaveVoiceChannel";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Leave Voice Channel",
        Group = "Discord",
        Icon = "\U000F03FF",
        Description = "Leaves the current voice channel"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            if (rpc.IsReady && tracker.Channel == null)
            {
                CommandFeedback.Show(ctx, ctx.Host.Tr(RpcErrorMapper.NotInVoiceChannel));
                return;
            }

            await rpc.CommandAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex, RpcErrorContext.SelectChannel);
        }
    }
}

/// <summary>Shows the name of the voice channel you are in.</summary>
internal sealed class CurrentVoiceChannelCommand(IDiscordRpc rpc, VoiceStateTracker tracker) : IDisplayCommand
{
    public const string Name = "Discord.CurrentVoiceChannel";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Current Voice Channel",
        Group = "Discord",
        Icon = "\U000F05CB",
        Description = "Shows the voice channel you are in and how many people are there"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    public string GetText(CommandContext ctx)
    {
        if (!rpc.IsReady) return "—";
        if (tracker.Channel is not VoiceChannelInfo channel) return ctx.Host.Tr("No voice channel");

        int count = tracker.Members.Count;
        return count > 0 ? $"{channel.Name}\n👥 {count}" : channel.Name;
    }

    public Task Execute(CommandContext ctx)
    {
        try
        {
            CommandFeedback.Show(ctx, tracker.Channel?.Name ?? ctx.Host.Tr(RpcErrorMapper.NotInVoiceChannel));
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Name} failed", ex);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Shows who is speaking in your voice channel right now.</summary>
internal sealed class SpeakingCommand(IDiscordRpc rpc, VoiceStateTracker tracker) : IDisplayCommand
{
    public const string Name = "Discord.Speaking";

    private const int MaxNames = 3;

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Who Is Speaking",
        Group = "Discord",
        Icon = "\U000F05CB",
        Description = "Shows who is speaking in your voice channel"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public string GetText(CommandContext ctx)
    {
        if (!rpc.IsReady) return "—";
        if (tracker.Channel == null) return ctx.Host.Tr("No voice channel");

        List<string> names = tracker.SpeakingUserIds
            .Select(id => tracker.FindMember(id)?.DisplayName)
            .OfType<string>()
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (names.Count == 0) return "—";
        return names.Count <= MaxNames
            ? string.Join("\n", names)
            : string.Join("\n", names.Take(MaxNames)) + $"\n+{names.Count - MaxNames}";
    }

    public Task Execute(CommandContext ctx) => Task.CompletedTask;
}
