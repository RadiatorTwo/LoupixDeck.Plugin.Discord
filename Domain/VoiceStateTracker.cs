using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Domain;

[Flags]
internal enum VoiceChange
{
    None = 0,
    Settings = 1,
    Channel = 2,
    Speaking = 4
}

/// <summary>
/// Live cache of the user's voice situation, shared by the voice features: voice settings
/// (VOICE_SETTINGS_UPDATE), the selected voice channel and its members (VOICE_CHANNEL_SELECT,
/// VOICE_STATE_*), and who is speaking (SPEAKING_START/STOP). Buttons render from this cache —
/// render calls are synchronous and must not wait for Discord.
/// </summary>
/// <remarks>Needs the <c>rpc.voice.read</c> scope.</remarks>
internal sealed class VoiceStateTracker : IDisposable
{
    public static readonly string[] Scopes = ["rpc.voice.read"];

    private static readonly string[] ChannelEvents =
        ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP"];

    private readonly IDiscordRpc _rpc;
    private readonly IPluginLogger _logger;
    private readonly Lock _gate = new();
    private readonly List<IDisposable> _globalSubscriptions = [];
    private readonly List<IDisposable> _channelSubscriptions = [];
    private readonly HashSet<string> _speaking = [];
    private readonly Dictionary<string, VoiceMember> _members = [];
    private string? _subscribedChannelId;

    public VoiceStateTracker(IDiscordRpc rpc, IPluginLogger logger)
    {
        _rpc = rpc;
        _logger = logger;

        _globalSubscriptions.Add(rpc.Subscribe("VOICE_SETTINGS_UPDATE", null, OnVoiceSettingsUpdate));
        _globalSubscriptions.Add(rpc.Subscribe("VOICE_CHANNEL_SELECT", null, OnVoiceChannelSelect));

        rpc.Authenticated += OnAuthenticated;
        rpc.ConnectionLost += OnConnectionLost;
    }

    public VoiceSettingsSnapshot? Settings { get; private set; }

    /// <summary>The selected voice channel, or null when the user is in none.</summary>
    public VoiceChannelInfo? Channel { get; private set; }

    /// <summary>Raised on a background thread whenever something in the cache changed.</summary>
    public event Action<VoiceChange>? Changed;

    public IReadOnlyCollection<string> SpeakingUserIds
    {
        get { lock (_gate) return _speaking.ToList(); }
    }

    public IReadOnlyList<VoiceMember> Members
    {
        get { lock (_gate) return _members.Values.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(); }
    }

    public VoiceMember? FindMember(string userId)
    {
        lock (_gate) return _members.GetValueOrDefault(userId);
    }

    /// <summary>Takes a SET_VOICE_SETTINGS reply (the full settings) into the cache.</summary>
    public void ApplyVoiceSettings(JsonElement data)
    {
        if (VoiceSettingsSnapshot.Parse(data) is not VoiceSettingsSnapshot settings) return;
        Settings = settings;
        Raise(VoiceChange.Settings);
    }

    /// <summary>Records local per-user settings after a SET_USER_VOICE_SETTINGS.</summary>
    public void ApplyUserSettings(string userId, int? volume, bool? mute)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(userId, out VoiceMember? member)) return;
            _members[userId] = member with
            {
                Volume = volume ?? member.Volume,
                LocalMute = mute ?? member.LocalMute
            };
        }

        Raise(VoiceChange.Channel);
    }

    /// <summary>Current voice settings — from the cache, or asked from Discord if not known yet.</summary>
    public async Task<VoiceSettingsSnapshot> GetSettingsAsync()
    {
        if (Settings is VoiceSettingsSnapshot cached) return cached;

        JsonElement data = await _rpc.CommandAsync("GET_VOICE_SETTINGS").ConfigureAwait(false);
        ApplyVoiceSettings(data);
        return Settings ?? throw new InvalidOperationException("GET_VOICE_SETTINGS returned no settings.");
    }

    private async void OnAuthenticated()
    {
        try
        {
            ApplyVoiceSettings(await _rpc.CommandAsync("GET_VOICE_SETTINGS").ConfigureAwait(false));
            await RefreshChannelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Reading the Discord voice state failed: {ex.Message}");
        }
    }

    private void OnConnectionLost()
    {
        Settings = null;
        SetChannel(null);
    }

    private void OnVoiceSettingsUpdate(JsonElement data) => ApplyVoiceSettings(data);

    private void OnVoiceChannelSelect(JsonElement data)
    {
        // The event only carries IDs; fetch the channel with its members off the reader thread.
        if (DiscordRpcClient.GetString(data, "channel_id") == null)
        {
            SetChannel(null);
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await RefreshChannelAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warn($"Reading the Discord voice channel failed: {ex.Message}"); }
        });
    }

    private async Task RefreshChannelAsync()
    {
        JsonElement data = await _rpc.CommandAsync("GET_SELECTED_VOICE_CHANNEL").ConfigureAwait(false);
        SetChannel(VoiceChannelInfo.Parse(data));
    }

    private void SetChannel(VoiceChannelInfo? channel)
    {
        lock (_gate)
        {
            Channel = channel;
            _speaking.Clear();
            _members.Clear();
            foreach (VoiceMember member in channel?.Members ?? [])
                _members[member.UserId] = member;
        }

        ResubscribeChannelEvents(channel?.Id);
        Raise(VoiceChange.Channel | VoiceChange.Speaking);
    }

    /// <summary>Per-channel events take the channel ID as argument; move them along with the user.</summary>
    private void ResubscribeChannelEvents(string? channelId)
    {
        List<IDisposable> old;
        lock (_gate)
        {
            if (_subscribedChannelId == channelId) return;
            _subscribedChannelId = channelId;
            old = [.. _channelSubscriptions];
            _channelSubscriptions.Clear();
        }

        foreach (IDisposable subscription in old)
            subscription.Dispose();

        if (channelId == null) return;

        List<IDisposable> added = [];
        foreach (string evt in ChannelEvents)
        {
            string eventName = evt;
            added.Add(_rpc.Subscribe(eventName, new JsonObject { ["channel_id"] = channelId },
                data => OnChannelEvent(channelId, eventName, data)));
        }

        lock (_gate)
            _channelSubscriptions.AddRange(added);
    }

    private void OnChannelEvent(string channelId, string evt, JsonElement data)
    {
        lock (_gate)
        {
            // An event for a channel we already left (delivered before the UNSUBSCRIBE took effect).
            if (_subscribedChannelId != channelId) return;
            string? eventChannel = DiscordRpcClient.GetString(data, "channel_id");
            if (eventChannel != null && eventChannel != channelId) return;
        }

        switch (evt)
        {
            case "SPEAKING_START":
            case "SPEAKING_STOP":
                if (DiscordRpcClient.GetString(data, "user_id") is not string userId) return;
                lock (_gate)
                {
                    if (evt == "SPEAKING_START") _speaking.Add(userId);
                    else _speaking.Remove(userId);
                }
                Raise(VoiceChange.Speaking);
                break;

            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                if (VoiceMember.Parse(data) is not VoiceMember member) return;
                lock (_gate) _members[member.UserId] = member;
                Raise(VoiceChange.Channel);
                break;

            case "VOICE_STATE_DELETE":
                if (VoiceMember.Parse(data) is not VoiceMember gone) return;
                lock (_gate)
                {
                    _members.Remove(gone.UserId);
                    _speaking.Remove(gone.UserId);
                }
                Raise(VoiceChange.Channel | VoiceChange.Speaking);
                break;
        }
    }

    private void Raise(VoiceChange change)
    {
        try { Changed?.Invoke(change); }
        catch (Exception ex) { _logger.Error("Discord voice state handler failed", ex); }
    }

    public void Dispose()
    {
        _rpc.Authenticated -= OnAuthenticated;
        _rpc.ConnectionLost -= OnConnectionLost;
        ResubscribeChannelEvents(null);
        foreach (IDisposable subscription in _globalSubscriptions)
            subscription.Dispose();
    }
}
