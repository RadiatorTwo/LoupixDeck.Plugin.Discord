using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>
/// Icons and the icon-with-caption layout the commands create on a touch button (SDK 1.27
/// <see cref="CommandDescriptor.ButtonLayout"/>). One constant per role, so a command, its layout and
/// the drawn state icons cannot drift apart.
/// </summary>
internal static class DiscordButtonLayouts
{
    // Material Design Icons code points (for descriptors) — checked against the MDI catalog.
    // MDI dropped its brand icons, so there is no Discord logo; a headset stands for the plugin.
    public const string Headset = "\U000F02CE";            // mdi-headset
    public const string Connection = "\U000F0318";         // mdi-lan-connect
    public const string Microphone = "\U000F036C";         // mdi-microphone
    public const string MicrophoneOff = "\U000F036D";      // mdi-microphone-off
    public const string Headphones = "\U000F02CB";         // mdi-headphones
    public const string HeadphonesOff = "\U000F07CE";      // mdi-headphones-off
    public const string VolumeHigh = "\U000F057E";         // mdi-volume-high
    public const string PushToTalk = "\U000F0D77";         // mdi-gesture-tap-hold
    public const string UserVoice = "\U000F05CB";          // mdi-account-voice
    public const string UserVoiceOff = "\U000F0ED4";       // mdi-account-voice-off
    public const string JoinCall = "\U000F0659";           // mdi-phone-plus
    public const string LeaveCall = "\U000F03F5";          // mdi-phone-hangup
    public const string Group = "\U000F0849";              // mdi-account-group
    public const string TextChannel = "\U000F0423";        // mdi-pound
    public const string Sound = "\U000F0387";              // mdi-music-note
    public const string ScreenShare = "\U000F1483";        // mdi-monitor-share
    public const string Video = "\U000F0567";              // mdi-video

    // Symbol names for IRenderCanvas.DrawSymbol (the stateful commands draw their own icon).
    public const string MicrophoneSymbol = "microphone";
    public const string MicrophoneOffSymbol = "microphone-off";
    public const string HeadphonesSymbol = "headphones";
    public const string HeadphonesOffSymbol = "headphones-off";
    public const string VoiceActivitySymbol = "waveform";
    public const string PushToTalkSymbol = "gesture-tap-hold";
    public const string MonitorSymbol = "monitor";
    public const string ScreenShareSymbol = "monitor-share";
    public const string VideoSymbol = "video";
    public const string VideoOffSymbol = "video-off";

    // Pixel values for a 90 px key; the host scales them onto the key actually being written.
    public const double IconScale = 0.5;
    public const int IconOffsetY = -9;
    public const int CaptionSize = 11;
    public const int CaptionOffsetY = 27;
    public const int CaptionBoxWidth = 88;
    public const int CaptionBoxHeight = 22;

    // The tall variant leaves room for two lines, for display commands whose text is replaced at
    // runtime by something long (a channel name, the people speaking).
    private const double TallIconScale = 0.36;
    private const int TallIconOffsetY = -19;
    private const int TallCaptionOffsetY = 17;
    private const int TallCaptionBoxHeight = 40;

    /// <summary>Set to <c>host.Tr</c> in Initialize, before the commands are created.</summary>
    internal static Func<string, string> Translate { get; set; } = static english => english;

    public static ButtonLayoutDescriptor IconWithCaption(string glyph, string? caption, bool tall = false) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol,
                Glyph = glyph,
                IconScale = tall ? TallIconScale : IconScale,
                OffsetY = tall ? TallIconOffsetY : IconOffsetY
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text,
                Text = caption == null ? null : Translate(caption),
                TextSize = CaptionSize,
                OffsetY = tall ? TallCaptionOffsetY : CaptionOffsetY,
                BoxWidth = CaptionBoxWidth,
                BoxHeight = tall ? TallCaptionBoxHeight : CaptionBoxHeight
            }
        ]
    };

    /// <summary>For commands that draw the whole key per state: no static layers underneath.</summary>
    public static ButtonLayoutDescriptor DrawnByCommand { get; } = new() { Mode = ButtonLayoutMode.None };
}
