namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// RPC error codes as documented under "Opcodes and Status Codes → RPC Error Codes".
/// They arrive as <c>{"evt":"ERROR","data":{"code":…,"message":…}}</c> in the reply to a command.
/// </summary>
internal static class RpcErrorCodes
{
    public const int UnknownError = 1000;
    public const int InvalidPayload = 4000;
    public const int InvalidCommand = 4002;
    public const int InvalidGuild = 4003;
    public const int InvalidEvent = 4004;
    public const int InvalidChannel = 4005;
    public const int InvalidPermissions = 4006;
    public const int InvalidClientId = 4007;
    public const int InvalidOrigin = 4008;
    public const int InvalidToken = 4009;
    public const int InvalidUser = 4010;
    public const int OAuth2Error = 5000;
    public const int SelectChannelTimedOut = 5001;
    public const int GetGuildTimedOut = 5002;
    public const int SelectVoiceForceRequired = 5003;
    public const int CaptureShortcutAlreadyListening = 5004;
}

/// <summary>
/// RPC close codes ("Opcodes and Status Codes → RPC Close Event Codes"), sent in an IPC CLOSE frame.
/// </summary>
internal static class RpcCloseCodes
{
    public const int InvalidClientId = 4000;
    public const int InvalidOrigin = 4001;
    public const int RateLimited = 4002;
    public const int TokenRevoked = 4003;
    public const int InvalidVersion = 4004;
    public const int InvalidEncoding = 4005;
}

/// <summary>Discord answered a command with <c>evt: "ERROR"</c>.</summary>
internal sealed class RpcException(string command, int code, string message)
    : Exception($"{command} failed: {code} {message}")
{
    public string Command { get; } = command;
    public int Code { get; } = code;
    public string RpcMessage { get; } = message;
}

/// <summary>Discord did not answer a command within its timeout.</summary>
internal sealed class RpcTimeoutException(string command, TimeSpan timeout)
    : Exception($"{command} timed out after {timeout.TotalSeconds:0} s.")
{
    public string Command { get; } = command;
}

/// <summary>The command could not be sent: there is no (authenticated) connection to Discord.</summary>
internal sealed class DiscordNotConnectedException(string message) : Exception(message);
