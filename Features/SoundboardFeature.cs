using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>A soundboard sound (Soundboard docs, "Soundboard Sound Object").</summary>
internal sealed record SoundboardSound(string SoundId, string Name, string? EmojiName, string? GuildId)
{
    /// <summary>Parses one sound object; shared by the RPC reply and the REST route.</summary>
    public static SoundboardSound? Parse(JsonElement sound)
    {
        string? id = DiscordRpcClient.GetString(sound, "sound_id");
        if (id == null) return null;

        return new SoundboardSound(id, DiscordRpcClient.GetString(sound, "name") ?? id,
            DiscordRpcClient.GetString(sound, "emoji_name"), DiscordRpcClient.GetString(sound, "guild_id"));
    }
}

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
/// Plays a sound with the RPC command <c>PLAY_SOUNDBOARD_SOUND</c>.
/// </summary>
/// <remarks>
/// <b>Not in Discord's RPC docs.</b> The command name and the argument keys <c>sound_id</c> and
/// <c>guild_id</c> come from the official Elgato Stream Deck Discord plugin (2.4.0), which uses
/// them over the same IPC interface. Verified against the Discord client (October 2026); being
/// undocumented, it may change without notice — the RPC tester in the settings helps re-check.
/// </remarks>
internal sealed class RpcSoundboardPlayer(IDiscordRpc rpc) : ISoundboardPlayer
{
    public const string PlayCommand = "PLAY_SOUNDBOARD_SOUND";

    public bool IsSupported => true;

    public async Task PlayAsync(SoundboardSound sound, CancellationToken ct)
    {
        JsonObject args = new() { ["sound_id"] = sound.SoundId };
        // Built-in sounds have no server; sending an empty guild_id would make them a different sound.
        if (!string.IsNullOrEmpty(sound.GuildId))
            args["guild_id"] = sound.GuildId;

        await rpc.CommandAsync(PlayCommand, args, ct: ct).ConfigureAwait(false);
    }
}

/// <summary>
/// All sounds the user can play — built-in and from their servers — via the RPC command
/// <c>GET_SOUNDBOARD_SOUNDS</c> (undocumented, see <see cref="RpcSoundboardPlayer"/>).
/// </summary>
/// <remarks>
/// The reply's shape is not documented either: both a plain array and an object with a
/// <c>sounds</c> array are accepted.
/// </remarks>
internal sealed class RpcSoundboardCatalog(IDiscordRpc rpc) : ISoundboardCatalog
{
    public async Task<IReadOnlyList<SoundboardSound>> GetSoundsAsync(CancellationToken ct)
    {
        JsonElement data = await rpc.CommandAsync("GET_SOUNDBOARD_SOUNDS", ct: ct).ConfigureAwait(false);

        JsonElement list = data.ValueKind == JsonValueKind.Array
            ? data
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sounds", out JsonElement sounds)
                ? sounds
                : default;

        if (list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Select(SoundboardSound.Parse).OfType<SoundboardSound>().ToList();
    }
}

/// <summary>
/// Discord's built-in sounds via the documented REST route <c>GET /soundboard-default-sounds</c>
/// ("soundboard sound objects that can be used by all users"); no token is sent. Fallback for when
/// <see cref="RpcSoundboardCatalog"/> fails.
/// </summary>
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

        return doc.RootElement.EnumerateArray().Select(SoundboardSound.Parse).OfType<SoundboardSound>().ToList();
    }
}

/// <summary>Soundboard buttons: sounds are picked in the menu, grouped by server.</summary>
internal sealed class SoundboardFeature : IDiscordFeature, IDisposable
{
    private readonly IDiscordRpc _rpc;
    private readonly GuildDirectory _guilds;
    private readonly ISoundboardPlayer _player;
    private readonly ISoundboardCatalog _catalog;
    private readonly ISoundboardCatalog _fallbackCatalog = new DefaultSoundsCatalog();
    private readonly IPluginLogger _logger;
    private IReadOnlyList<SoundboardSound> _sounds = [];

    public SoundboardFeature(IDiscordRpc rpc, VoiceStateTracker tracker, GuildDirectory guilds, IPluginLogger logger)
    {
        _rpc = rpc;
        _guilds = guilds;
        _logger = logger;
        _player = new RpcSoundboardPlayer(rpc);
        _catalog = new RpcSoundboardCatalog(rpc);
        Commands = [new PlaySoundboardSoundCommand(_player, tracker, () => _sounds)];

        rpc.Authenticated += LoadSoundsInBackground;
    }

    // The Elgato plugin requests no soundboard-specific scope; playing happens in the voice
    // channel, so the voice scopes are requested to be safe.
    public IReadOnlyCollection<string> RequiredScopes => [.. VoiceStateTracker.Scopes, "rpc.voice.write"];

    public IEnumerable<IPluginCommand> Commands { get; }

    public IEnumerable<MenuNode> GetMenuNodes(ButtonTargets target)
    {
        IReadOnlyList<SoundboardSound> sounds = _sounds;
        if (sounds.Count == 0) return [];

        List<MenuNode> groups = sounds
            .GroupBy(s => s.GuildId ?? string.Empty)
            .Select(g => new MenuNode
            {
                Name = GroupName(g.Key),
                Children = g.Select(ToNode).ToList()
            })
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // A single group needs no extra folder level.
        IReadOnlyList<MenuNode> children = groups.Count == 1 ? groups[0].Children : groups;
        return [new MenuNode { Name = "Soundboard", Children = children }];
    }

    private string GroupName(string guildId) =>
        guildId.Length == 0
            ? "Discord"
            : _guilds.Guilds.FirstOrDefault(g => g.Id == guildId)?.Name ?? guildId;

    private static MenuNode ToNode(SoundboardSound s) => new()
    {
        Name = string.IsNullOrEmpty(s.EmojiName) ? s.Name : $"{s.EmojiName} {s.Name}",
        CommandName = PlaySoundboardSoundCommand.Name,
        Parameters = new Dictionary<string, string>
        {
            ["soundId"] = s.SoundId,
            ["guildId"] = s.GuildId ?? string.Empty
        }
    };

    /// <summary>Fresh list on every menu build — sounds have no change event over RPC.</summary>
    public Task RefreshForMenuAsync(CancellationToken ct) => LoadSoundsAsync(ct);

    private void LoadSoundsInBackground() => _ = Task.Run(() => LoadSoundsAsync(CancellationToken.None));

    private async Task LoadSoundsAsync(CancellationToken ct)
    {
        try
        {
            _sounds = await _catalog.GetSoundsAsync(ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // Menu timeout: keep the cached list.
        }
        catch (Exception ex)
        {
            _logger.Warn($"GET_SOUNDBOARD_SOUNDS failed ({ex.Message}); falling back to the built-in sounds.");
        }

        // Only fill an empty list; a stale server list beats replacing it with built-ins only.
        if (_sounds.Count > 0) return;

        try { _sounds = await _fallbackCatalog.GetSoundsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Warn($"Loading Discord soundboard sounds failed: {ex.Message}"); }
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
        Icon = DiscordButtonLayouts.Sound,
        ButtonLayout = DiscordButtonLayouts.IconWithCaption(DiscordButtonLayouts.Sound, "Soundboard"),
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
            if (tracker.Channel == null)
            {
                CommandFeedback.Show(ctx, ctx.Host.Tr(RpcErrorMapper.NotInVoiceChannel));
                return;
            }

            string? guildId = ctx.Parameters.Length > 1 && ctx.Parameters[1].Trim().Length > 0
                ? ctx.Parameters[1].Trim()
                : null;
            SoundboardSound sound = sounds().FirstOrDefault(s => s.SoundId == soundId && s.GuildId == guildId)
                                    ?? new SoundboardSound(soundId, soundId, null, guildId);
            await player.PlayAsync(sound, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommandFeedback.ShowError(ctx, Name, ex);
        }
    }
}
