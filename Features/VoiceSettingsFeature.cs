using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// The user's own voice settings: microphone mute, deafen, input and output volume
/// (GET_VOICE_SETTINGS / SET_VOICE_SETTINGS, live via VOICE_SETTINGS_UPDATE).
/// </summary>
/// <remarks>
/// Only one app at a time may change voice settings over RPC; the first one to do so locks them
/// for all others until it disconnects (RPC docs, SET_VOICE_SETTINGS). Failures of the SET calls
/// are therefore reported with <see cref="RpcErrorContext.VoiceSettingsWrite"/>.
/// </remarks>
internal sealed class VoiceSettingsFeature : IDiscordFeature, IDisposable
{
    private readonly VoiceStateTracker _tracker;
    private readonly IPluginHost _host;

    public VoiceSettingsFeature(IDiscordRpc rpc, VoiceStateTracker tracker, IPluginHost host)
    {
        _tracker = tracker;
        _host = host;
        Commands =
        [
            new MuteCommand(rpc, tracker),
            new DeafenCommand(rpc, tracker),
            new VoiceVolumeCommand(rpc, tracker, VoiceVolumeCommand.Direction.Input),
            new VoiceVolumeCommand(rpc, tracker, VoiceVolumeCommand.Direction.Output)
        ];

        tracker.Changed += OnVoiceChanged;
    }

    public IReadOnlyCollection<string> RequiredScopes => [.. VoiceStateTracker.Scopes, "rpc.voice.write"];

    public IEnumerable<IPluginCommand> Commands { get; }

    private void OnVoiceChanged(VoiceChange change)
    {
        if (!change.HasFlag(VoiceChange.Settings) || _tracker.Settings is not VoiceSettingsSnapshot settings)
            return;

        DiscordStates.Push(_host, MuteCommand.Name, settings.Mute);
        DiscordStates.Push(_host, DeafenCommand.Name, settings.Deaf);
        _host.RequestButtonRefresh(VoiceVolumeCommand.InputName);
        _host.RequestButtonRefresh(VoiceVolumeCommand.OutputName);
    }

    public void Dispose() => _tracker.Changed -= OnVoiceChanged;

    /// <summary>Sends SET_VOICE_SETTINGS and takes the reply (the full settings) into the cache.</summary>
    internal static async Task SetAsync(IDiscordRpc rpc, VoiceStateTracker tracker, JsonObject args)
    {
        JsonElement data = await rpc.CommandAsync("SET_VOICE_SETTINGS", args).ConfigureAwait(false);
        tracker.ApplyVoiceSettings(data);
    }
}

internal sealed class MuteCommand(IDiscordRpc rpc, VoiceStateTracker tracker) : DiscordStatefulCommand
{
    public const string Name = "Discord.Mute";

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Microphone Mute",
        Group = "Discord",
        Icon = "\U000F036D",
        Description = "Mutes or unmutes your microphone in Discord",
        ParameterTemplate = "({mode})",
        Parameters = [new CommandParameter("mode", typeof(ToggleMode)) { DefaultValue = nameof(ToggleMode.Toggle) }],
        States = DiscordStates.Toggle
    };

    protected override IReadOnlyDictionary<string, StateVisual> Visuals => DiscordStates.MuteVisuals;

    public override async Task Execute(CommandContext ctx)
    {
        try
        {
            VoiceSettingsSnapshot current = await tracker.GetSettingsAsync().ConfigureAwait(false);
            bool mute = DiscordStates.Resolve(DiscordStates.ParseMode(ctx), current.Mute);

            try
            {
                await VoiceSettingsFeature.SetAsync(rpc, tracker, new JsonObject { ["mute"] = mute }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CommandFeedback.ShowError(ctx, Name, ex, RpcErrorContext.VoiceSettingsWrite);
            }
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex);
        }
    }
}

internal sealed class DeafenCommand(IDiscordRpc rpc, VoiceStateTracker tracker) : DiscordStatefulCommand
{
    public const string Name = "Discord.Deafen";

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Deafen",
        Group = "Discord",
        Icon = "\U000F07CE",
        Description = "Deafens or undeafens you in Discord (no sound, microphone muted)",
        ParameterTemplate = "({mode})",
        Parameters = [new CommandParameter("mode", typeof(ToggleMode)) { DefaultValue = nameof(ToggleMode.Toggle) }],
        States = DiscordStates.Toggle
    };

    protected override IReadOnlyDictionary<string, StateVisual> Visuals => DiscordStates.DeafenVisuals;

    public override async Task Execute(CommandContext ctx)
    {
        try
        {
            VoiceSettingsSnapshot current = await tracker.GetSettingsAsync().ConfigureAwait(false);
            bool deaf = DiscordStates.Resolve(DiscordStates.ParseMode(ctx), current.Deaf);

            try
            {
                await VoiceSettingsFeature.SetAsync(rpc, tracker, new JsonObject { ["deaf"] = deaf }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CommandFeedback.ShowError(ctx, Name, ex, RpcErrorContext.VoiceSettingsWrite);
            }
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex);
        }
    }
}

/// <summary>
/// Input (microphone, 0–100) or output (speaker, 0–200) volume on a dial; pressing the dial
/// toggles mute (input) or deafen (output).
/// </summary>
internal sealed class VoiceVolumeCommand : IAdjustmentCommand
{
    public const string InputName = "Discord.InputVolume";
    public const string OutputName = "Discord.OutputVolume";

    private readonly IDiscordRpc _rpc;
    private readonly VoiceStateTracker _tracker;
    private readonly Direction _direction;
    private readonly Lock _gate = new();
    private double? _pending;

    public VoiceVolumeCommand(IDiscordRpc rpc, VoiceStateTracker tracker, Direction direction)
    {
        _rpc = rpc;
        _tracker = tracker;
        _direction = direction;

        bool input = direction == Direction.Input;
        Descriptor = new CommandDescriptor
        {
            CommandName = input ? InputName : OutputName,
            DisplayName = input ? "Discord: Input Volume" : "Discord: Output Volume",
            Group = "Discord",
            Icon = input ? "\U000F036C" : "\U000F057E",
            Description = input
                ? "Turn to change your microphone volume in Discord, press to mute"
                : "Turn to change the Discord output volume, press to deafen",
            ParameterTemplate = "({step})",
            Parameters = [new CommandParameter("step", typeof(int)) { DefaultValue = "5" }]
        };
    }

    public enum Direction
    {
        Input,
        Output
    }

    public CommandDescriptor Descriptor { get; }

    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;

    private double Max => _direction == Direction.Input
        ? VoiceSettingsSnapshot.MaxInputVolume
        : VoiceSettingsSnapshot.MaxOutputVolume;

    public async Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        try
        {
            VoiceSettingsSnapshot settings = await _tracker.GetSettingsAsync().ConfigureAwait(false);
            int step = ctx.Parameters.Length > 0 && int.TryParse(ctx.Parameters[0], out int s) && s > 0 ? s : 5;

            // A fast turn fires several adjustments before Discord confirms the first one; build on
            // the value we last sent, not on the stale cache.
            double next;
            lock (_gate)
            {
                double current = _pending ?? Current(settings);
                next = Math.Clamp(current + (ticks * step), 0, Max);
                _pending = next;
            }

            string key = _direction == Direction.Input ? "input" : "output";
            try
            {
                await VoiceSettingsFeature.SetAsync(_rpc, _tracker,
                    new JsonObject { [key] = new JsonObject { ["volume"] = next } }).ConfigureAwait(false);
                CommandFeedback.Show(ctx, $"{Math.Round(next)}%");
            }
            catch (Exception ex)
            {
                CommandFeedback.ShowError(ctx, Descriptor.CommandName, ex, RpcErrorContext.VoiceSettingsWrite);
            }
            finally
            {
                lock (_gate)
                {
                    if (_pending == next) _pending = null;
                }
            }
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Descriptor.CommandName, ex);
        }
    }

    public async Task ApplyReset(CommandContext ctx)
    {
        try
        {
            VoiceSettingsSnapshot settings = await _tracker.GetSettingsAsync().ConfigureAwait(false);
            JsonObject args = _direction == Direction.Input
                ? new JsonObject { ["mute"] = !settings.Mute }
                : new JsonObject { ["deaf"] = !settings.Deaf };

            try
            {
                await VoiceSettingsFeature.SetAsync(_rpc, _tracker, args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CommandFeedback.ShowError(ctx, Descriptor.CommandName, ex, RpcErrorContext.VoiceSettingsWrite);
            }
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Descriptor.CommandName, ex);
        }
    }

    /// <summary>Off a dial (macro, CLI) the command acts as the press does.</summary>
    public Task Execute(CommandContext ctx) => ApplyReset(ctx);

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        if (_tracker.Settings is not VoiceSettingsSnapshot settings) return null;

        double volume = Current(settings);
        bool off = _direction == Direction.Input ? settings.Mute : settings.Deaf;
        return new AdjustmentValue(volume / Max, off ? "🔇" : $"{Math.Round(volume)}%");
    }

    private double Current(VoiceSettingsSnapshot settings) =>
        _direction == Direction.Input ? settings.InputVolume : settings.OutputVolume;
}
