using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Relay;

/// <summary>Lectura y escritura de mensajes del relay con el límite de <see cref="ProtocolInfo.MaxRelayMessageBytes"/>.</summary>
internal static class RelayWire
{
    /// <summary>
    /// Lee un mensaje. Devuelve null si la conexión termina. La longitud se comprueba contra el límite del relay
    /// antes de reservar memoria; si lo pasa, lanza <see cref="ProtocolException"/>.
    /// </summary>
    public static async Task<IMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[FrameCodec.TcpHeaderSize];
        try
        {
            await stream.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        FrameCodec.ParseTcpHeader(header, out int payloadLength, out ushort type);
        if (payloadLength > ProtocolInfo.MaxRelayMessageBytes)
            throw new ProtocolException($"Mensaje demasiado grande para el relay: {payloadLength} bytes");

        var payload = new byte[payloadLength];
        try
        {
            await stream.ReadExactlyAsync(payload, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        return FrameCodec.DecodePayload(type, payload, 0, payloadLength);
    }

    public static async Task WriteAsync(Stream stream, IMessage message, CancellationToken ct)
    {
        byte[] frame = FrameCodec.EncodeTcp(message);
        await stream.WriteAsync(frame, ct);
    }

    /// <summary>Errores normales de una conexión que se corta o que manda basura.</summary>
    public static bool IsConnectionError(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException
            or InvalidOperationException or ProtocolException or ArgumentException;
}
