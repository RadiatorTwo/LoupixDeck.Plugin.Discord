using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>What a toggle command does when pressed.</summary>
/// <remarks>The names are persisted in button bindings — never rename them. The first one is the default.</remarks>
internal enum ToggleMode
{
    Toggle,
    On,
    Off
}

/// <summary>How one state is drawn: an MDI symbol (by name) with an English caption below it.</summary>
internal readonly record struct StateVisual(string Symbol, string Caption, PluginColor Color);

/// <summary>Button states the toggle commands declare; the host creates them when the command is assigned.</summary>
internal static class DiscordStates
{
    public const string Off = "Off";
    public const string On = "On";

    private static readonly PluginColor Normal = PluginColor.White;
    private static readonly PluginColor Red = PluginColor.FromRgb(0xED, 0x42, 0x45);
    private static readonly PluginColor Green = PluginColor.FromRgb(0x23, 0xA5, 0x5A);

    public static IReadOnlyList<ButtonStateDescriptor> Toggle { get; } =
    [
        new() { Name = Off, Description = "Off" },
        new() { Name = On, Description = "On" }
    ];

    public static IReadOnlyDictionary<string, StateVisual> MuteVisuals { get; } = Pair(
        off: new(DiscordButtonLayouts.MicrophoneSymbol, "Mic", Normal),
        on: new(DiscordButtonLayouts.MicrophoneOffSymbol, "Muted", Red));

    public static IReadOnlyDictionary<string, StateVisual> DeafenVisuals { get; } = Pair(
        off: new(DiscordButtonLayouts.HeadphonesSymbol, "Sound", Normal),
        on: new(DiscordButtonLayouts.HeadphonesOffSymbol, "Deafened", Red));

    public static IReadOnlyDictionary<string, StateVisual> InputModeVisuals { get; } = Pair(
        off: new(DiscordButtonLayouts.VoiceActivitySymbol, "Voice", Normal),
        on: new(DiscordButtonLayouts.PushToTalkSymbol, "PTT", Normal));

    public static IReadOnlyDictionary<string, StateVisual> ScreenShareVisuals { get; } = Pair(
        off: new(DiscordButtonLayouts.MonitorSymbol, "Go Live", Normal),
        on: new(DiscordButtonLayouts.ScreenShareSymbol, "Live", Red));

    public static IReadOnlyDictionary<string, StateVisual> VideoVisuals { get; } = Pair(
        off: new(DiscordButtonLayouts.VideoOffSymbol, "Camera", Normal),
        on: new(DiscordButtonLayouts.VideoSymbol, "Camera on", Green));

    private static Dictionary<string, StateVisual> Pair(StateVisual off, StateVisual on) =>
        new(StringComparer.OrdinalIgnoreCase) { [Off] = off, [On] = on };

    public static ToggleMode ParseMode(CommandContext ctx, int index = 0) =>
        ctx.Parameters.Length > index && Enum.TryParse(ctx.Parameters[index], true, out ToggleMode mode)
            ? mode
            : ToggleMode.Toggle;

    public static bool Resolve(ToggleMode mode, bool current) => mode switch
    {
        ToggleMode.On => true,
        ToggleMode.Off => false,
        _ => !current
    };

    /// <summary>Shows <paramref name="on"/> on every button bound to <paramref name="commandName"/>.</summary>
    public static void Push(IPluginHost host, string commandName, bool on)
    {
        try
        {
            host.SetActiveButtonState(commandName, on ? On : Off);
        }
        catch (Exception ex)
        {
            // The host may not be ready yet, or the button was unbound meanwhile.
            host.Logger.Warn($"Could not update button state for {commandName}: {ex.Message}");
        }
    }
}

/// <summary>
/// Base for commands with declared On/Off states: the feature pushes the live Discord state with
/// <see cref="IPluginHost.SetActiveButtonState"/>, this class draws icon and caption for it.
/// </summary>
internal abstract class DiscordStatefulCommand : IDisplayImageCommand
{
    public abstract CommandDescriptor Descriptor { get; }

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton | ButtonTargets.SimpleButton;

    /// <summary>Changes arrive as pushes; polling is only a slow safety net.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    public abstract Task Execute(CommandContext ctx);

    protected abstract IReadOnlyDictionary<string, StateVisual> Visuals { get; }

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        // No declared state (the user manages the states himself) → leave the content alone.
        if (ctx.StateName == null || !Visuals.TryGetValue(ctx.StateName, out StateVisual visual))
            return false;

        // Same geometry as DiscordButtonLayouts.IconWithCaption, scaled from a 90 px key, so a drawn
        // state looks like the layout the other commands create.
        double scale = Math.Min(canvas.Width, canvas.Height) / 90.0;
        int iconSize = (int)Math.Round(90 * DiscordButtonLayouts.IconScale * scale);
        int iconX = (canvas.Width - iconSize) / 2;
        int iconY = ((canvas.Height - iconSize) / 2) + (int)Math.Round(DiscordButtonLayouts.IconOffsetY * scale);
        canvas.DrawSymbol(visual.Symbol, iconX, iconY, iconSize, iconSize, visual.Color);

        int boxWidth = (int)Math.Round(DiscordButtonLayouts.CaptionBoxWidth * scale);
        int boxHeight = (int)Math.Round(DiscordButtonLayouts.CaptionBoxHeight * scale);
        int boxY = ((canvas.Height - boxHeight) / 2) + (int)Math.Round(DiscordButtonLayouts.CaptionOffsetY * scale);
        canvas.DrawText(ctx.Host.Tr(visual.Caption), (canvas.Width - boxWidth) / 2, boxY, boxWidth, boxHeight,
            visual.Color, (float)(DiscordButtonLayouts.CaptionSize * scale));

        return true;
    }
}
