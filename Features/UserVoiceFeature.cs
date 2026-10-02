using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// Local volume and mute of other users (SET_USER_VOICE_SETTINGS). These only affect what you
/// hear. The people are picked live from your voice channel in a folder
/// (<see cref="VoiceUsersCommand"/>), or put on a dial through a dial preset.
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

        UserVoiceControl control = new(rpc, tracker);
        Commands =
        [
            new VoiceUsersCommand(rpc, tracker, control),
            new UserVolumeCommand(tracker, control),
            new UserMuteCommand(control)
        ];

        tracker.Changed += OnVoiceChanged;
    }

    public IReadOnlyCollection<string> RequiredScopes => [.. VoiceStateTracker.Scopes, "rpc.voice.write"];

    public IEnumerable<IPluginCommand> Commands { get; }

    /// <summary>One volume dial per person in your voice channel.</summary>
    public IEnumerable<DialPresetDescriptor> GetDialPresets() =>
        _tracker.Members
            .Where(m => m.UserId != _rpc.CurrentUserId)
            .Select(m => DiscordButtonLayouts.AdjustmentPreset(
                $"discord-user-{m.UserId}", $"Discord: {m.DisplayName}", DiscordButtonLayouts.UserVoice,
                UserVolumeCommand.Name, new Dictionary<string, string> { ["userId"] = m.UserId }));

    private void OnVoiceChanged(VoiceChange change)
    {
        if (change.HasFlag(VoiceChange.Channel))
            _host.RequestButtonRefresh(UserVolumeCommand.Name);
    }

    public void Dispose() => _tracker.Changed -= OnVoiceChanged;

    internal static string? UserId(CommandContext ctx) =>
        ctx.Parameters.Length > 0 && !string.IsNullOrWhiteSpace(ctx.Parameters[0]) ? ctx.Parameters[0].Trim() : null;
}

/// <summary>Volume and local mute of one user, shared by the commands and the folder.</summary>
internal sealed class UserVoiceControl(IDiscordRpc rpc, VoiceStateTracker tracker)
{
    // Volumes set for users who have left the channel since: their next adjustment builds on it.
    private readonly ConcurrentDictionary<string, int> _lastSet = new();

    public int CurrentVolume(string userId) =>
        _lastSet.TryGetValue(userId, out int known)
            ? known
            : tracker.FindMember(userId)?.Volume ?? VoiceMember.DefaultVolume;

    /// <summary>Changes the volume by <paramref name="delta"/> percent points and returns the new value.</summary>
    public async Task<int> AdjustAsync(string userId, int delta)
    {
        int next = Math.Clamp(CurrentVolume(userId) + delta, 0, VoiceMember.MaxVolume);

        // Recorded before sending, so a fast turn continues from here instead of the stale cache.
        _lastSet[userId] = next;
        tracker.ApplyUserSettings(userId, next, null);

        try
        {
            await rpc.CommandAsync("SET_USER_VOICE_SETTINGS",
                new JsonObject { ["user_id"] = userId, ["volume"] = next }).ConfigureAwait(false);
            return next;
        }
        catch
        {
            _lastSet.TryRemove(userId, out _);
            throw;
        }
    }

    /// <summary>Toggles the local mute; null when the user is not in your voice channel (state unknown).</summary>
    public async Task<bool?> ToggleMuteAsync(string userId)
    {
        VoiceMember? member = tracker.FindMember(userId);
        if (member == null) return null;

        bool mute = !member.LocalMute;
        await rpc.CommandAsync("SET_USER_VOICE_SETTINGS",
            new JsonObject { ["user_id"] = userId, ["mute"] = mute }).ConfigureAwait(false);
        tracker.ApplyUserSettings(userId, null, mute);
        return mute;
    }
}

/// <summary>Opens a live folder with the people in your voice channel.</summary>
internal sealed class VoiceUsersCommand(IDiscordRpc rpc, VoiceStateTracker tracker, UserVoiceControl control)
    : IPluginCommand
{
    public const string Name = "Discord.VoiceUsers";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Voice Channel Users",
        Group = "Discord",
        Icon = DiscordButtonLayouts.Group,
        ButtonLayout = DiscordButtonLayouts.IconWithCaption(DiscordButtonLayouts.Group, "Users"),
        Description = "Opens the people in your voice channel: tap to select, tap again to mute them for you, turn the first dial to change their volume"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    public Task Execute(CommandContext ctx)
    {
        try
        {
            if (!rpc.IsReady)
                CommandFeedback.Show(ctx, ctx.Host.Tr(RpcErrorMapper.NotConnected));
            else if (tracker.Channel == null)
                CommandFeedback.Show(ctx, ctx.Host.Tr(RpcErrorMapper.NotInVoiceChannel));
            else
                ctx.Host.OpenFolder(new VoiceUsersFolderProvider(rpc, tracker, control, ctx.Host));
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Name} failed", ex);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Turn to change a user's local volume (0–200 %), press to mute them locally.</summary>
internal sealed class UserVolumeCommand(VoiceStateTracker tracker, UserVoiceControl control) : IAdjustmentCommand
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
        try
        {
            int next = await control.AdjustAsync(userId, ticks * step).ConfigureAwait(false);
            CommandFeedback.Show(ctx, $"{tracker.FindMember(userId)?.DisplayName} {next}%".Trim());
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex, RpcErrorContext.VoiceSettingsWrite);
        }
    }

    public Task ApplyReset(CommandContext ctx) => UserMuteCommand.ToggleAsync(ctx, control, Name);

    public Task Execute(CommandContext ctx) => ApplyReset(ctx);

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        if (UserVoiceFeature.UserId(ctx) is not string userId) return null;

        int volume = control.CurrentVolume(userId);
        string text = tracker.FindMember(userId)?.LocalMute == true ? "🔇" : $"{volume}%";
        return new AdjustmentValue((double)volume / VoiceMember.MaxVolume, text);
    }
}

/// <summary>Mutes or unmutes a user for you only (for macros and the command line; the folder covers buttons).</summary>
internal sealed class UserMuteCommand(UserVoiceControl control) : IPluginCommand
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

    public Task Execute(CommandContext ctx) => ToggleAsync(ctx, control, Name);

    internal static async Task ToggleAsync(CommandContext ctx, UserVoiceControl control, string commandName)
    {
        if (UserVoiceFeature.UserId(ctx) is not string userId) return;

        try
        {
            bool? mute = await control.ToggleMuteAsync(userId).ConfigureAwait(false);
            if (mute == null)
            {
                // The current local mute is only known for users in your voice channel.
                CommandFeedback.Show(ctx, ctx.Host.Tr("User is not in your voice channel"));
                return;
            }

            CommandFeedback.Show(ctx, mute.Value ? "🔇" : "🔊");
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, commandName, ex, RpcErrorContext.VoiceSettingsWrite);
        }
    }
}
