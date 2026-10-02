using System.Text.Json;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// Camera and screen share ("Go Live") toggles: <c>TOGGLE_VIDEO</c> / <c>TOGGLE_SCREENSHARE</c>,
/// live state via <c>VIDEO_STATE_UPDATE</c> / <c>SCREENSHARE_STATE_UPDATE</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not in Discord's RPC docs.</b> Command, event and scope names come from the official Elgato
/// Stream Deck Discord plugin (2.4.0). The commands take no arguments there (the actions have no
/// settings); the event payload is expected to carry an <c>active</c> flag — if it does not, the
/// payload is logged once so the field can be corrected.
/// </para>
/// <para>
/// The scopes <c>rpc.video.*</c> and <c>rpc.screenshare.*</c> are not in the OAuth2 docs either,
/// so they are optional: if Discord refuses them, the plugin connects without them and only
/// these two buttons are unavailable.
/// </para>
/// </remarks>
internal sealed class MediaFeature : IDiscordFeature, IDisposable
{
    public const string ScreenshareReadScope = "rpc.screenshare.read";
    public const string ScreenshareWriteScope = "rpc.screenshare.write";
    public const string VideoReadScope = "rpc.video.read";
    public const string VideoWriteScope = "rpc.video.write";

    private static readonly string[] Scopes =
        [VideoReadScope, VideoWriteScope, ScreenshareReadScope, ScreenshareWriteScope];

    private readonly IDiscordRpc _rpc;
    private readonly IPluginHost _host;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _payloadLogged;

    public MediaFeature(IDiscordRpc rpc, VoiceStateTracker tracker, IPluginHost host)
    {
        _rpc = rpc;
        _host = host;
        Commands =
        [
            new MediaToggleCommand(rpc, tracker, MediaToggleCommand.Kind.Screenshare),
            new MediaToggleCommand(rpc, tracker, MediaToggleCommand.Kind.Video)
        ];

        rpc.Authenticated += SyncSubscriptions;
    }

    public IReadOnlyCollection<string> RequiredScopes => [];

    public IReadOnlyCollection<string> OptionalScopes => Scopes;

    public IEnumerable<IPluginCommand> Commands { get; }

    /// <summary>
    /// Subscribes only with the matching permission (without it Discord rejects the SUBSCRIBE);
    /// runs after every (re)authentication, as the granted scopes may have changed.
    /// </summary>
    private void SyncSubscriptions()
    {
        lock (_subscriptions)
        {
            foreach (IDisposable subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();

            if (_rpc.HasScope(ScreenshareReadScope))
                _subscriptions.Add(_rpc.Subscribe("SCREENSHARE_STATE_UPDATE", null,
                    data => OnStateUpdate(MediaToggleCommand.ScreenshareName, "SCREENSHARE_STATE_UPDATE", data)));
            if (_rpc.HasScope(VideoReadScope))
                _subscriptions.Add(_rpc.Subscribe("VIDEO_STATE_UPDATE", null,
                    data => OnStateUpdate(MediaToggleCommand.VideoName, "VIDEO_STATE_UPDATE", data)));
        }
    }

    private void OnStateUpdate(string commandName, string evt, JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("active", out JsonElement active)
            && active.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            DiscordStates.Push(_host, commandName, active.GetBoolean());
            return;
        }

        if (_payloadLogged) return;
        _payloadLogged = true;
        _host.Logger.Info($"{evt} without 'active' flag: {RpcDebugLog.Redact(data.GetRawText())}");
    }

    public void Dispose()
    {
        _rpc.Authenticated -= SyncSubscriptions;
        lock (_subscriptions)
        {
            foreach (IDisposable subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}

internal sealed class MediaToggleCommand : DiscordStatefulCommand
{
    public const string ScreenshareName = "Discord.ToggleScreenshare";
    public const string VideoName = "Discord.ToggleVideo";
    public const string PermissionHint = "Discord did not grant this permission";
    public const string ConfirmHint = "Confirm the permission in Discord";

    private static readonly IReadOnlyDictionary<string, StateVisual> ScreenshareVisuals =
        DiscordStates.Visuals("LIVE", "SHARE");

    private static readonly IReadOnlyDictionary<string, StateVisual> VideoVisuals =
        DiscordStates.Visuals("CAM", "CAM");

    private readonly IDiscordRpc _rpc;
    private readonly VoiceStateTracker _tracker;
    private readonly string _rpcCommand;
    private readonly string _writeScope;

    public MediaToggleCommand(IDiscordRpc rpc, VoiceStateTracker tracker, Kind kind)
    {
        _rpc = rpc;
        _tracker = tracker;
        bool share = kind == Kind.Screenshare;
        _rpcCommand = share ? "TOGGLE_SCREENSHARE" : "TOGGLE_VIDEO";
        _writeScope = share ? MediaFeature.ScreenshareWriteScope : MediaFeature.VideoWriteScope;
        Visuals = share ? ScreenshareVisuals : VideoVisuals;
        Descriptor = new CommandDescriptor
        {
            CommandName = share ? ScreenshareName : VideoName,
            DisplayName = share ? "Discord: Screen Share" : "Discord: Camera",
            Group = "Discord",
            Icon = share ? "\U000F0379" : "\U000F0567",
            Description = share
                ? "Starts or stops streaming in the voice channel — the detected game directly, otherwise Discord asks what to share"
                : "Turns your camera on or off in the voice channel",
            States = DiscordStates.Toggle
        };
    }

    public enum Kind
    {
        Screenshare,
        Video
    }

    public override CommandDescriptor Descriptor { get; }

    protected override IReadOnlyDictionary<string, StateVisual> Visuals { get; }

    public override async Task Execute(CommandContext ctx)
    {
        try
        {
            bool reauthorized = false;
            if (_rpc.IsReady && !_rpc.HasScope(_writeScope))
            {
                // The press is the user's go-ahead: ask Discord for the permission right here.
                CommandFeedback.Show(ctx, ctx.Host.Tr(ConfirmHint));
                if (!await _rpc.EnsureScopeAsync(_writeScope).ConfigureAwait(false))
                {
                    // Simple buttons have no display for the overlay; leave a trace in the log too.
                    ctx.Host.Logger.Warn($"{Descriptor.CommandName}: scope {_writeScope} not granted.");
                    CommandFeedback.Show(ctx, ctx.Host.Tr(PermissionHint));
                    return;
                }

                reauthorized = true;
            }

            // Right after re-authorizing the connection is new and the voice cache still empty;
            // let Discord decide whether a voice channel is active.
            if (!reauthorized && _rpc.IsReady && _tracker.Channel == null)
            {
                CommandFeedback.Show(ctx, ctx.Host.Tr(RpcErrorMapper.NotInVoiceChannel));
                return;
            }

            await _rpc.CommandAsync(_rpcCommand).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Descriptor.CommandName, ex);
        }
    }
}
