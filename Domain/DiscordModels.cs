using System.Text.Json;
using LoupixDeck.Plugin.Discord.Rpc;

namespace LoupixDeck.Plugin.Discord.Domain;

/// <summary>
/// The parts of the voice settings the features use (RPC docs, "GET_VOICE_SETTINGS").
/// Input volume ranges 0–100, output volume 0–200.
/// </summary>
internal sealed record VoiceSettingsSnapshot(bool Mute, bool Deaf, double InputVolume, double OutputVolume)
{
    public const double MaxInputVolume = 100;
    public const double MaxOutputVolume = 200;

    public static VoiceSettingsSnapshot? Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        return new VoiceSettingsSnapshot(
            Json.Bool(data, "mute"),
            Json.Bool(data, "deaf"),
            data.TryGetProperty("input", out JsonElement input) ? Json.Double(input, "volume") : 0,
            data.TryGetProperty("output", out JsonElement output) ? Json.Double(output, "volume") : 0);
    }
}

/// <summary>
/// One entry of a channel's <c>voice_states</c> (GET_CHANNEL / VOICE_STATE_* events): the user,
/// and the local per-user settings this client applies to them (volume 0–200, local mute).
/// </summary>
internal sealed record VoiceMember(string UserId, string DisplayName, int Volume, bool LocalMute)
{
    public const int MaxVolume = 200;
    public const int DefaultVolume = 100;

    public static VoiceMember? Parse(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("user", out JsonElement user))
            return null;

        string? id = DiscordRpcClient.GetString(user, "id");
        if (id == null) return null;

        string name = DiscordRpcClient.GetString(state, "nick")
                      ?? DiscordRpcClient.GetString(user, "global_name")
                      ?? DiscordRpcClient.GetString(user, "username")
                      ?? id;

        int volume = state.TryGetProperty("volume", out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(v.GetDouble())
            : DefaultVolume;

        return new VoiceMember(id, name, volume, Json.Bool(state, "mute"));
    }
}

/// <summary>The selected voice channel with the people in it.</summary>
internal sealed record VoiceChannelInfo(string Id, string? GuildId, string Name, IReadOnlyList<VoiceMember> Members)
{
    public static VoiceChannelInfo? Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        string? id = DiscordRpcClient.GetString(data, "id");
        if (id == null) return null;

        List<VoiceMember> members = [];
        if (data.TryGetProperty("voice_states", out JsonElement states) && states.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement state in states.EnumerateArray())
            {
                if (VoiceMember.Parse(state) is VoiceMember member)
                    members.Add(member);
            }
        }

        return new VoiceChannelInfo(id, DiscordRpcClient.GetString(data, "guild_id"),
            DiscordRpcClient.GetString(data, "name") ?? id, members);
    }
}

internal sealed record GuildEntry(string Id, string Name, IReadOnlyList<ChannelEntry> Channels);

internal sealed record ChannelEntry(string Id, string Name, int Type)
{
    // Channel types (Channel resource docs). The RPC docs list 0 and 2; announcement and stage
    // channels are their text/voice counterparts and are selectable the same way.
    public const int GuildText = 0;
    public const int GuildVoice = 2;
    public const int GuildAnnouncement = 5;
    public const int GuildStageVoice = 13;

    public bool IsVoice => Type is GuildVoice or GuildStageVoice;

    public bool IsText => Type is GuildText or GuildAnnouncement;
}

internal static class Json
{
    public static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;

    public static double Double(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
}
