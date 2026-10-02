using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Domain;

/// <summary>
/// Cached servers and their channels (GET_GUILDS + GET_CHANNELS), for the channel pickers in the
/// command menu. Loaded after authentication and again when Discord reports a new server or
/// channel; the menu itself only reads the cache, so it never waits on Discord.
/// </summary>
internal sealed class GuildDirectory : IDisposable
{
    private readonly IDiscordRpc _rpc;
    private readonly IPluginLogger _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gate = new();
    private Task? _refresh;
    private IReadOnlyList<GuildEntry> _guilds = [];

    public GuildDirectory(IDiscordRpc rpc, IPluginLogger logger)
    {
        _rpc = rpc;
        _logger = logger;

        _subscriptions.Add(rpc.Subscribe("GUILD_CREATE", null, _ => RefreshInBackground()));
        _subscriptions.Add(rpc.Subscribe("CHANNEL_CREATE", null, _ => RefreshInBackground()));

        rpc.Authenticated += RefreshInBackground;
        rpc.ConnectionLost += OnConnectionLost;
    }

    public IReadOnlyList<GuildEntry> Guilds => _guilds;

    /// <summary>Menu nodes: one folder per server, with the channels that pass <paramref name="filter"/>.</summary>
    public List<MenuNode> BuildChannelMenu(Func<ChannelEntry, bool> filter, string commandName, string parameterName)
    {
        List<MenuNode> nodes = [];
        foreach (GuildEntry guild in _guilds)
        {
            List<MenuNode> channels = guild.Channels
                .Where(filter)
                .Select(c => new MenuNode
                {
                    Name = c.Name,
                    CommandName = commandName,
                    Parameters = new Dictionary<string, string> { [parameterName] = c.Id }
                })
                .ToList();

            if (channels.Count > 0)
                nodes.Add(new MenuNode { Name = guild.Name, Children = channels });
        }

        return nodes;
    }

    /// <summary>Looks up a channel name for display; null when unknown.</summary>
    public string? FindChannelName(string channelId) =>
        _guilds.SelectMany(g => g.Channels).FirstOrDefault(c => c.Id == channelId)?.Name;

    private void OnConnectionLost() => _guilds = [];

    private void RefreshInBackground() => _ = RefreshAsync(CancellationToken.None);

    /// <summary>
    /// Reloads servers and channels. Callers arriving while a refresh runs share it, so several
    /// features asking at once (menu build) cost one round of requests. <paramref name="ct"/> only
    /// bounds the caller's wait; the shared refresh finishes and updates the cache regardless.
    /// </summary>
    public Task RefreshAsync(CancellationToken ct)
    {
        Task refresh;
        lock (_gate)
        {
            if (_refresh == null || _refresh.IsCompleted)
                _refresh = Task.Run(LoadAsync);
            refresh = _refresh;
        }

        return refresh.WaitAsync(ct);
    }

    private async Task LoadAsync()
    {
        try
        {
            JsonElement data = await _rpc.CommandAsync("GET_GUILDS").ConfigureAwait(false);
            if (!data.TryGetProperty("guilds", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return;

            List<(string Id, string Name)> guilds = [];
            foreach (JsonElement guild in list.EnumerateArray())
            {
                if (DiscordRpcClient.GetString(guild, "id") is string id)
                    guilds.Add((id, DiscordRpcClient.GetString(guild, "name") ?? id));
            }

            // Channels of all servers in parallel; one round-trip per server is the slow part.
            IReadOnlyList<ChannelEntry>[] channels =
                await Task.WhenAll(guilds.Select(g => LoadChannelsAsync(g.Id))).ConfigureAwait(false);

            _guilds = guilds.Select((g, i) => new GuildEntry(g.Id, g.Name, channels[i])).ToList();
        }
        catch (Exception ex)
        {
            // Keep the previous lists; a failed refresh must not empty the pickers.
            _logger.Warn($"Loading Discord servers failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<ChannelEntry>> LoadChannelsAsync(string guildId)
    {
        try
        {
            JsonElement data = await _rpc.CommandAsync("GET_CHANNELS", new JsonObject { ["guild_id"] = guildId })
                .ConfigureAwait(false);
            if (!data.TryGetProperty("channels", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return [];

            List<ChannelEntry> channels = [];
            foreach (JsonElement channel in list.EnumerateArray())
            {
                string? id = DiscordRpcClient.GetString(channel, "id");
                if (id == null) continue;
                channels.Add(new ChannelEntry(id, DiscordRpcClient.GetString(channel, "name") ?? id,
                    DiscordRpcClient.GetInt(channel, "type")));
            }

            return channels;
        }
        catch (RpcException ex)
        {
            // One inaccessible server must not empty the whole picker.
            _logger.Warn($"GET_CHANNELS for a server failed: {ex.Code} {ex.RpcMessage}");
            return [];
        }
    }

    public void Dispose()
    {
        _rpc.Authenticated -= RefreshInBackground;
        _rpc.ConnectionLost -= OnConnectionLost;
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
    }
}
