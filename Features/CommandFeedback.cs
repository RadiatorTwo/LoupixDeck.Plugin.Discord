using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Features;

/// <summary>Short-lived feedback on the device — the SDK has no toast, so it goes on the touch display.</summary>
internal static class CommandFeedback
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1800);

    /// <summary>
    /// Shows <paramref name="text"/> on the touch slot that triggered the command, or next to the
    /// rotary encoder. Simple buttons have no display of their own; the text only goes to the log.
    /// </summary>
    public static void Show(CommandContext ctx, string text)
    {
        try
        {
            int slot = ctx.Target switch
            {
                ButtonTargets.TouchButton when ctx.SourceIndex is int touch => touch,
                ButtonTargets.RotaryEncoder when ctx.SourceIndex is int rotary => ctx.Host.GetTouchSlotForRotary(rotary),
                _ => -1
            };

            if (slot >= 0)
                ctx.Host.OverlayTouchText(slot, text, Duration);
        }
        catch
        {
            // Feedback is cosmetic; never let it fail a command.
        }
    }

    /// <summary>Logs the failure of <paramref name="commandName"/> and shows a readable reason.</summary>
    public static void ShowError(CommandContext ctx, string commandName, Exception ex,
        RpcErrorContext context = RpcErrorContext.General)
    {
        string message = RpcErrorMapper.Describe(ex, context, ctx.Host.Tr);

        // Expected conditions (not connected, Discord said no) are not errors in the log sense.
        if (ex is RpcException or DiscordNotConnectedException or RpcTimeoutException)
            ctx.Host.Logger.Warn($"{commandName}: {ex.Message}");
        else
            ctx.Host.Logger.Error($"{commandName} failed", ex);

        Show(ctx, message);
    }
}
