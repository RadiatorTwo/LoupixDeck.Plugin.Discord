namespace LoupixDeck.Plugin.Discord.Transport;

/// <summary>Opcodes of the Discord IPC frame header (RPC docs, "RPC over IPC").</summary>
internal enum IpcOpcode : uint
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4
}
