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

internal readonly record struct StateVisual(string Label, PluginColor Accent, bool Active);

/// <summary>Button states the toggle commands declare; the host creates them when the command is assigned.</summary>
internal static class DiscordStates
{
    public const string Off = "Off";
    public const string On = "On";

    private static readonly PluginColor Inactive = PluginColor.FromRgb(0x60, 0x66, 0x70);
    private static readonly PluginColor Blurple = PluginColor.FromRgb(0x58, 0x65, 0xF2);
    private static readonly PluginColor Red = PluginColor.FromRgb(0xED, 0x42, 0x45);

    public static IReadOnlyList<ButtonStateDescriptor> Toggle { get; } =
    [
        new() { Name = Off, Description = "Off" },
        new() { Name = On, Description = "On" }
    ];

    public static IReadOnlyDictionary<string, StateVisual> MuteVisuals { get; } =
        new Dictionary<string, StateVisual>(StringComparer.OrdinalIgnoreCase)
        {
            [Off] = new("MIC", Blurple, false),
            [On] = new("MUTED", Red, true)
        };

    public static IReadOnlyDictionary<string, StateVisual> DeafenVisuals { get; } =
        new Dictionary<string, StateVisual>(StringComparer.OrdinalIgnoreCase)
        {
            [Off] = new("AUDIO", Blurple, false),
            [On] = new("DEAF", Red, true)
        };

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
/// <see cref="IPluginHost.SetActiveButtonState"/>, this class draws the indicator for it.
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

        int size = Math.Min(canvas.Width, canvas.Height);
        int radius = Math.Max(6, size / 6);
        int centerX = canvas.Width / 2;
        int centerY = (canvas.Height / 2) - (size / 10);

        if (visual.Active)
            canvas.FillCircle(centerX, centerY, radius, visual.Accent);
        else
            canvas.DrawCircle(centerX, centerY, radius, Math.Max(2, radius / 4), visual.Accent);

        int labelHeight = Math.Max(14, size / 4);
        canvas.DrawText(visual.Label, 0, canvas.Height - labelHeight - (size / 12), canvas.Width, labelHeight,
            visual.Accent, size / 6f, bold: true);

        return true;
    }
}
