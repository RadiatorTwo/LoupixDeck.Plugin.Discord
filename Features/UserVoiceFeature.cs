using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// Local volume and mute of other users (SET_USER_VOICE_SETTINGS). These only affect what you
/// hear; the users are picked from the voice channel you are in.
/// </summary>
internal sealed class UserVoiceFeature : IDiscordFeature, IDisposable
{
    private readonly IDiscordRpc _rpc;
    private readonly VoiceStateTracker _tracker;
    private readonly IPluginHost _host;

    public UserVoiceFeature(IDiscordRpc rpc, VoiceStateTracker tracker, IPluginHost host)
    {
        _rpc = rpc;
        _tracker = tracker;
        _host = host;

        // Volumes set for users who have left the channel since — their next adjustment builds on it.
        ConcurrentDictionary<string, int> lastSet = new();
        Commands =
        [
            new UserVolumeCommand(rpc, tracker, lastSet),
            new UserMuteCommand(rpc, tracker)
        ];

        tracker.Changed += OnVoiceChanged;
    }

    public IReadOnlyCollection<string> RequiredScopes => [.. VoiceStateTracker.Scopes, "rpc.voice.write"];

    public IEnumerable<IPluginCommand> Commands { get; }

    public IEnumerable<MenuNode> GetMenuNodes(ButtonTargets target)
    {
        List<VoiceMember> others = _tracker.Members.Where(m => m.UserId != _rpc.CurrentUserId).ToList();
        if (others.Count == 0) return [];

        // A dial gets the volume control, a button the local mute.
        string commandName = target.HasFlag(ButtonTargets.RotaryEncoder) ? UserVolumeCommand.Name : UserMuteCommand.Name;
        List<MenuNode> users = others
            .Select(m => new MenuNode
            {
                Name = m.DisplayName,
                CommandName = commandName,
                Parameters = new Dictionary<string, string> { ["userId"] = m.UserId }
            })
            .ToList();

        return [new MenuNode { Name = "Users in voice channel", Children = users }];
    }

    private void OnVoiceChanged(VoiceChange change)
    {
        if (change.HasFlag(VoiceChange.Channel))
            _host.RequestButtonRefresh(UserVolumeCommand.Name);
    }

    public void Dispose() => _tracker.Changed -= OnVoiceChanged;

    internal static string? UserId(CommandContext ctx) =>
        ctx.Parameters.Length > 0 && !string.IsNullOrWhiteSpace(ctx.Parameters[0]) ? ctx.Parameters[0].Trim() : null;
}

/// <summary>Turn to change a user's local volume (0–200 %), press to mute them locally.</summary>
internal sealed class UserVolumeCommand(IDiscordRpc rpc, VoiceStateTracker tracker,
    ConcurrentDictionary<string, int> lastSet) : IAdjustmentCommand
{
    public const string Name = "Discord.UserVolume";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: User Volume",
        Group = "Discord",
        Icon = DiscordButtonLayouts.UserVoice,
        Description = "Turn to change how loud you hear a user, press to mute them for you",
        HiddenFromMenu = true,
        ParameterTemplate = "({userId},{step})",
        Parameters =
        [
            new CommandParameter("userId", typeof(string)),
            new CommandParameter("step", typeof(int)) { DefaultValue = "10" }
        ]
    };

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;

    public async Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        if (UserVoiceFeature.UserId(ctx) is not string userId) return;

        int step = ctx.Parameters.Length > 1 && int.TryParse(ctx.Parameters[1], out int s) && s > 0 ? s : 10;
        int current = lastSet.TryGetValue(userId, out int known)
            ? known
            : tracker.FindMember(userId)?.Volume ?? VoiceMember.DefaultVolume;
        int next = Math.Clamp(current + (ticks * step), 0, VoiceMember.MaxVolume);

        // Recorded before sending, so a fast turn continues from here instead of the stale cache.
        lastSet[userId] = next;
        tracker.ApplyUserSettings(userId, next, null);

        try
        {
            await rpc.CommandAsync("SET_USER_VOICE_SETTINGS",
                new JsonObject { ["user_id"] = userId, ["volume"] = next }).ConfigureAwait(false);
            CommandFeedback.Show(ctx, $"{MemberName(userId)} {next}%");
        }
        catch (Exception ex)
        {
            lastSet.TryRemove(userId, out _);
            CommandFeedback.ShowError(ctx, Name, ex, RpcErrorContext.VoiceSettingsWrite);
        }
    }

    public Task ApplyReset(CommandContext ctx) => UserMuteCommand.ToggleAsync(ctx, rpc, tracker, Name);

    public Task Execute(CommandContext ctx) => ApplyReset(ctx);

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        if (UserVoiceFeature.UserId(ctx) is not string userId) return null;

        VoiceMember? member = tracker.FindMember(userId);
        int volume = member?.Volume ?? (lastSet.TryGetValue(userId, out int known) ? known : VoiceMember.DefaultVolume);
        string text = member?.LocalMute == true ? "🔇" : $"{volume}%";
        return new AdjustmentValue((double)volume / VoiceMember.MaxVolume, text);
    }

    private string MemberName(string userId) => tracker.FindMember(userId)?.DisplayName ?? string.Empty;
}

/// <summary>Mutes or unmutes a user for you only.</summary>
internal sealed class UserMuteCommand(IDiscordRpc rpc, VoiceStateTracker tracker) : IPluginCommand
{
    public const string Name = "Discord.UserMute";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Mute User",
        Group = "Discord",
        Icon = DiscordButtonLayouts.UserVoiceOff,
        ButtonLayout = DiscordButtonLayouts.IconWithCaption(DiscordButtonLayouts.UserVoiceOff, "Mute User"),
        Description = "Mutes or unmutes a user for you only",
        HiddenFromMenu = true,
        ParameterTemplate = "({userId})",
        Parameters = [new CommandParameter("userId", typeof(string))]
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public Task Execute(CommandContext ctx) => ToggleAsync(ctx, rpc, tracker, Name);

    internal static async Task ToggleAsync(CommandContext ctx, IDiscordRpc rpc, VoiceStateTracker tracker,
        string commandName)
    {
        if (UserVoiceFeature.UserId(ctx) is not string userId) return;

        try
        {
            VoiceMember? member = tracker.FindMember(userId);
            if (member == null)
            {
                // The current local mute is only known for users in your voice channel.
                CommandFeedback.Show(ctx, ctx.Host.Tr("User is not in your voice channel"));
                return;
            }

            bool mute = !member.LocalMute;
            await rpc.CommandAsync("SET_USER_VOICE_SETTINGS",
                new JsonObject { ["user_id"] = userId, ["mute"] = mute }).ConfigureAwait(false);
            tracker.ApplyUserSettings(userId, null, mute);
            CommandFeedback.Show(ctx, mute ? $"🔇 {member.DisplayName}" : $"🔊 {member.DisplayName}");
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, commandName, ex, RpcErrorContext.VoiceSettingsWrite);
        }
    }
}
