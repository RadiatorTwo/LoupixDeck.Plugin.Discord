namespace LoupixDeck.Plugin.Discord.Rpc;

internal enum DiscordConnectionStatus
{
    NotConfigured,
    DiscordNotRunning,
    Connecting,
    NotAuthorized,
    Connected,
    Error
}

/// <summary>
/// What the user sees as "the connection": transport and authentication folded into one state.
/// <see cref="Detail"/> is the user name when connected, else an already translated reason.
/// </summary>
internal sealed record DiscordConnectionState(DiscordConnectionStatus Status, string? Detail = null)
{
    public bool IsConnected => Status == DiscordConnectionStatus.Connected;

    public string Describe(Func<string, string> tr) => Status switch
    {
        DiscordConnectionStatus.NotConfigured => tr("Not configured — enter the client ID in the plugin settings"),
        DiscordConnectionStatus.DiscordNotRunning => tr(RpcErrorMapper.NotRunning),
        DiscordConnectionStatus.Connecting => tr("Connecting…"),
        DiscordConnectionStatus.NotAuthorized => Detail ?? tr(RpcErrorMapper.NotAuthorized),
        DiscordConnectionStatus.Connected => Detail == null
            ? tr("Connected")
            : string.Format(tr("Connected as {0}"), Detail),
        _ => Detail ?? tr("Connection error")
    };
}
