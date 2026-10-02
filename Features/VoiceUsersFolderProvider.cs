using System.Text;
using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// The people in your voice channel, live: one tile per person with their volume (or a mute
/// sign), highlighted while they speak. Tapping a tile selects the person, tapping the selected
/// one again mutes them for you. The first dial changes the selected person's volume, its press
/// toggles the mute.
/// </summary>
internal sealed class VoiceUsersFolderProvider : FolderProviderBase
{
    private const int Step = 10;

    private static readonly PluginColor Tile = PluginColor.FromRgb(0x2B, 0x2D, 0x31);
    private static readonly PluginColor SelectedTile = PluginColor.FromRgb(0x3C, 0x45, 0xA5);
    private static readonly PluginColor SpeakingTile = PluginColor.FromRgb(0x1F, 0x7A, 0x45);
    private static readonly PluginColor MutedText = PluginColor.FromRgb(0x8E, 0x92, 0x97);

    private readonly IDiscordRpc _rpc;
    private readonly VoiceStateTracker _tracker;
    private readonly UserVoiceControl _control;
    private readonly IPluginHost _host;
    private readonly Dictionary<int, RotaryOverride> _rotaries;
    private string? _selectedUserId;
    private string? _rendered;

    public VoiceUsersFolderProvider(IDiscordRpc rpc, VoiceStateTracker tracker, UserVoiceControl control,
        IPluginHost host)
    {
        _rpc = rpc;
        _tracker = tracker;
        _control = control;
        _host = host;
        _rotaries = new Dictionary<int, RotaryOverride>
        {
            [0] = new RotaryOverride
            {
                OnLeft = () => AdjustSelectedAsync(-Step),
                OnRight = () => AdjustSelectedAsync(+Step),
                OnPress = ToggleSelectedAsync
            }
        };
    }

    public override string Title => _tracker.Channel?.Name ?? _host.Tr("Voice Channel");

    public override IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides => _rotaries;

    public override void OnEnter()
    {
        // No announcement here: the host builds the entries right after OnEnter returns.
        _rendered = Signature();
        _tracker.Changed += OnVoiceChanged;
    }

    public override void OnExit() => _tracker.Changed -= OnVoiceChanged;

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        List<VoiceMember> others = Others();
        EnsureSelection(others);

        if (others.Count == 0)
        {
            string hint = _tracker.Channel == null
                ? _host.Tr(RpcErrorMapper.NotInVoiceChannel)
                : _host.Tr("No one else is here");
            return [new FolderEntry { SlotIndex = 0, Text = hint, TextSize = 14, BackColor = Tile }];
        }

        HashSet<string> speaking = [.. _tracker.SpeakingUserIds];
        FolderGridInfo grid = _host.FolderGrid;
        List<FolderEntry> entries = [];

        for (int i = 0; i < others.Count; i++)
        {
            int slot = grid.SlotForIndex(i);
            if (slot < 0) break; // grid full

            VoiceMember member = others[i];
            bool selected = member.UserId == _selectedUserId;
            string level = member.LocalMute ? "🔇" : $"{member.Volume}%";

            entries.Add(new FolderEntry
            {
                SlotIndex = slot,
                Text = $"{member.DisplayName}\n{level}",
                TextSize = 14,
                Bold = selected,
                BackColor = speaking.Contains(member.UserId) ? SpeakingTile : selected ? SelectedTile : Tile,
                TextColor = member.LocalMute ? MutedText : PluginColor.White,
                OnPress = () => PressAsync(member.UserId)
            });
        }

        return entries;
    }

    private async Task PressAsync(string userId)
    {
        if (_selectedUserId != userId)
        {
            _selectedUserId = userId;
            RaiseIfChanged();
            return;
        }

        await ToggleSelectedAsync().ConfigureAwait(false);
    }

    private async Task AdjustSelectedAsync(int delta)
    {
        if (_selectedUserId is not string userId) return;

        try { await _control.AdjustAsync(userId, delta).ConfigureAwait(false); }
        catch (Exception ex) { _host.Logger.Warn($"Changing a user's volume failed: {ex.Message}"); }
        RaiseIfChanged();
    }

    private async Task ToggleSelectedAsync()
    {
        if (_selectedUserId is not string userId) return;

        try { await _control.ToggleMuteAsync(userId).ConfigureAwait(false); }
        catch (Exception ex) { _host.Logger.Warn($"Muting a user failed: {ex.Message}"); }
        RaiseIfChanged();
    }

    private void OnVoiceChanged(VoiceChange change)
    {
        if (change.HasFlag(VoiceChange.Channel) || change.HasFlag(VoiceChange.Speaking))
            RaiseIfChanged();
    }

    private List<VoiceMember> Others() =>
        _tracker.Members.Where(m => m.UserId != _rpc.CurrentUserId).ToList();

    /// <summary>Keeps a valid selection: the first person when nobody (or someone who left) is selected.</summary>
    private void EnsureSelection(List<VoiceMember> others)
    {
        if (_selectedUserId == null || others.All(m => m.UserId != _selectedUserId))
            _selectedUserId = others.Count > 0 ? others[0].UserId : null;
    }

    /// <summary>Redraws only when something visible changed; speaking events arrive many times a second.</summary>
    private void RaiseIfChanged()
    {
        string signature = Signature();
        if (signature == _rendered) return;
        _rendered = signature;
        RaiseEntriesChanged();
    }

    private string Signature()
    {
        List<VoiceMember> others = Others();
        EnsureSelection(others);
        HashSet<string> speaking = [.. _tracker.SpeakingUserIds];

        StringBuilder builder = new();
        builder.Append(_tracker.Channel?.Id).Append('|').Append(_selectedUserId).Append('|');
        foreach (VoiceMember m in others)
        {
            builder.Append(m.UserId).Append(':').Append(m.DisplayName).Append(':').Append(m.Volume)
                .Append(':').Append(m.LocalMute).Append(':').Append(speaking.Contains(m.UserId)).Append(';');
        }

        return builder.ToString();
    }
}
