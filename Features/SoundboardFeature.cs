using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>A soundboard sound (Soundboard docs, "Soundboard Sound Object").</summary>
internal sealed record SoundboardSound(string SoundId, string Name, string? EmojiName, string? GuildId);

/// <summary>Plays a soundboard sound in the voice channel the user is in.</summary>
internal interface ISoundboardPlayer
{
    bool IsSupported { get; }

    Task PlayAsync(SoundboardSound sound, CancellationToken ct);
}

/// <summary>Lists the sounds that can be offered on buttons.</summary>
internal interface ISoundboardCatalog
{
    Task<IReadOnlyList<SoundboardSound>> GetSoundsAsync(CancellationToken ct);
}

/// <summary>
/// Playing soundboard sounds over RPC. <b>Not documented by Discord</b> — the RPC docs list no
/// soundboard command, so nothing is guessed here.
/// </summary>
/// <remarks>
/// TODO(soundboard): find the real command with the "Log RPC traffic" setting enabled, then
/// <list type="number">
///   <item>set <see cref="PlayCommand"/> to the command name,</item>
///   <item>fill <see cref="BuildArgs"/> with the observed argument names,</item>
///   <item>add the scope it needs to <see cref="SoundboardFeature.RequiredScopes"/> if Discord rejects it with 4006.</item>
/// </list>
/// The documented REST route <c>POST /channels/{channel.id}/send-soundboard-sound</c> is not an
/// option: the docs only describe it for bot authentication, and user tokens must not be used.
/// </remarks>
internal sealed class RpcSoundboardPlayer(IDiscordRpc rpc) : ISoundboardPlayer
{
    // TODO(soundboard): the RPC command that plays a sound; null while unknown.
    private static readonly string? PlayCommand = null;

    public bool IsSupported => PlayCommand != null;

    public async Task PlayAsync(SoundboardSound sound, CancellationToken ct)
    {
        if (PlayCommand is not string command)
            throw new NotSupportedException("Playing soundboard sounds over RPC is not documented.");

        await rpc.CommandAsync(command, BuildArgs(sound), ct: ct).ConfigureAwait(false);
    }

    private static JsonObject BuildArgs(SoundboardSound sound) =>
        // TODO(soundboard): replace with the argument names observed in the debug log.
        throw new NotImplementedException($"Arguments for playing sound {sound.SoundId} are not known yet.");
}

/// <summary>
/// Discord's built-in sounds via the documented REST route <c>GET /soundboard-default-sounds</c>
/// ("soundboard sound objects that can be used by all users"); no token is sent.
/// </summary>
/// <remarks>
/// TODO(soundboard): server sounds (<c>GET /guilds/{guild.id}/soundboard-sounds</c>) are documented
/// for bot authentication only. Add them here once a supported way for user apps is known
/// (an RPC command seen in the debug log, for instance).
/// </remarks>
internal sealed class DefaultSoundsCatalog : ISoundboardCatalog
{
    private const string Endpoint = "https://discord.com/api/v10/soundboard-default-sounds";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<IReadOnlyList<SoundboardSound>> GetSoundsAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await Http.GetAsync(Endpoint, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        List<SoundboardSound> sounds = [];
        foreach (JsonElement sound in doc.RootElement.EnumerateArray())
        {
            string? id = DiscordRpcClient.GetString(sound, "sound_id");
            if (id == null) continue;

            sounds.Add(new SoundboardSound(id, DiscordRpcClient.GetString(sound, "name") ?? id,
                DiscordRpcClient.GetString(sound, "emoji_name"), DiscordRpcClient.GetString(sound, "guild_id")));
        }

        return sounds;
    }
}

/// <summary>
/// Soundboard buttons. Hidden until <see cref="ISoundboardPlayer.IsSupported"/> — the menu only
/// offers sounds once playing them actually works.
/// </summary>
internal sealed class SoundboardFeature : IDiscordFeature, IDisposable
{
    private readonly IDiscordRpc _rpc;
    private readonly ISoundboardPlayer _player;
    private readonly ISoundboardCatalog _catalog;
    private readonly IPluginLogger _logger;
    private IReadOnlyList<SoundboardSound> _sounds = [];

    public SoundboardFeature(IDiscordRpc rpc, VoiceStateTracker tracker, IPluginLogger logger)
    {
        _rpc = rpc;
        _logger = logger;
        _player = new RpcSoundboardPlayer(rpc);
        _catalog = new DefaultSoundsCatalog();
        Commands = [new PlaySoundboardSoundCommand(_player, tracker, () => _sounds)];

        rpc.Authenticated += LoadSoundsInBackground;
    }

    // TODO(soundboard): add the scope the play command needs once it is known.
    public IReadOnlyCollection<string> RequiredScopes => [];

    public IEnumerable<IPluginCommand> Commands { get; }

    public IEnumerable<MenuNode> GetMenuNodes(ButtonTargets target)
    {
        if (!_player.IsSupported || _sounds.Count == 0) return [];

        List<MenuNode> sounds = _sounds
            .Select(s => new MenuNode
            {
                Name = string.IsNullOrEmpty(s.EmojiName) ? s.Name : $"{s.EmojiName} {s.Name}",
                CommandName = PlaySoundboardSoundCommand.Name,
                Parameters = new Dictionary<string, string>
                {
                    ["soundId"] = s.SoundId,
                    ["guildId"] = s.GuildId ?? string.Empty
                }
            })
            .ToList();

        return [new MenuNode { Name = "Soundboard", Children = sounds }];
    }

    private void LoadSoundsInBackground()
    {
        if (!_player.IsSupported) return;

        _ = Task.Run(async () =>
        {
            try { _sounds = await _catalog.GetSoundsAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warn($"Loading Discord soundboard sounds failed: {ex.Message}"); }
        });
    }

    public void Dispose() => _rpc.Authenticated -= LoadSoundsInBackground;
}

internal sealed class PlaySoundboardSoundCommand(
    ISoundboardPlayer player,
    VoiceStateTracker tracker,
    Func<IReadOnlyList<SoundboardSound>> sounds) : IPluginCommand
{
    public const string Name = "Discord.PlaySoundboardSound";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Discord: Play Soundboard Sound",
        Group = "Discord",
        Icon = "\U000F0387",
        Description = "Plays a soundboard sound in your voice channel",
        HiddenFromMenu = true,
        ParameterTemplate = "({soundId},{guildId})",
        Parameters =
        [
            new CommandParameter("soundId", typeof(string)),
            new CommandParameter("guildId", typeof(string))
        ]
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        string? soundId = ctx.Parameters.Length > 0 ? ctx.Parameters[0].Trim() : null;
        if (string.IsNullOrEmpty(soundId)) return;

        try
        {
            if (!player.IsSupported)
            {
                ctx.Host.Logger.Warn($"{Name}: playing soundboard sounds is not implemented (undocumented RPC command).");
                CommandFeedback.Show(ctx, ctx.Host.Tr("Soundboard is not supported yet"));
                return;
            }

            if (tracker.Channel == null)
            {
                CommandFeedback.Show(ctx, ctx.Host.Tr(RpcErrorMapper.NotInVoiceChannel));
                return;
            }

            string? guildId = ctx.Parameters.Length > 1 && ctx.Parameters[1].Length > 0 ? ctx.Parameters[1].Trim() : null;
            SoundboardSound sound = sounds().FirstOrDefault(s => s.SoundId == soundId)
                                    ?? new SoundboardSound(soundId, soundId, null, guildId);
            await player.PlayAsync(sound, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex);
        }
    }
}
