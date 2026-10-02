using System.Buffers.Binary;
using System.Text;

namespace LoupixDeck.Plugin.Discord.Transport;

/// <summary>
/// One IPC frame: <c>opcode (uint32 LE) + length (uint32 LE) + UTF-8 JSON payload</c>.
/// </summary>
/// <remarks>
/// The RPC docs give the field layout but not the byte order; every Discord client and every known
/// RPC library uses little endian, so that is what is written and expected here.
/// </remarks>
internal sealed record IpcFrame(IpcOpcode Opcode, string Json)
{
    public const int HeaderSize = 8;

    /// <summary>Upper bound for a payload; anything larger means the stream is out of sync.</summary>
    public const int MaxPayloadSize = 16 * 1024 * 1024;

    public byte[] Encode()
    {
        byte[] payload = Encoding.UTF8.GetBytes(Json);
        byte[] buffer = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    public static async Task<IpcFrame> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        IpcOpcode opcode = (IpcOpcode)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (length > MaxPayloadSize)
            throw new InvalidDataException($"Discord IPC frame too large ({length} bytes).");

        byte[] payload = new byte[length];
        if (length > 0)
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        return new IpcFrame(opcode, Encoding.UTF8.GetString(payload));
    }
}
