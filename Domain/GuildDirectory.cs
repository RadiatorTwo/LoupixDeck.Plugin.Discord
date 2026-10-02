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
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
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

    private void RefreshInBackground() => _ = Task.Run(RefreshAsync);

    private async Task RefreshAsync()
    {
        // Coalesce bursts (several CHANNEL_CREATE in a row): a waiting refresh covers them all.
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            JsonElement data = await _rpc.CommandAsync("GET_GUILDS").ConfigureAwait(false);
            List<GuildEntry> guilds = [];

            if (data.TryGetProperty("guilds", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement guild in list.EnumerateArray())
                {
                    string? id = DiscordRpcClient.GetString(guild, "id");
                    if (id == null) continue;

                    guilds.Add(new GuildEntry(id, DiscordRpcClient.GetString(guild, "name") ?? id,
                        await LoadChannelsAsync(id).ConfigureAwait(false)));
                }
            }

            _guilds = guilds;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Loading Discord servers failed: {ex.Message}");
        }
        finally
        {
            _refreshLock.Release();
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
