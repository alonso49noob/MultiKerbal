using System.IO;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Net
{
    /// <summary>
    /// Formato en el cable:
    ///   TCP: [int32 longitud = 2 + payload][uint16 tipo][payload]
    ///   UDP: [uint64 token][uint16 tipo][payload]
    /// </summary>
    public static class FrameCodec
    {
        public const int TcpHeaderSize = 6;
        public const int UdpHeaderSize = 10;

        public static byte[] EncodeTcp(IMessage message)
        {
            var writer = new PacketWriter(64);
            writer.WriteInt32(0);
            writer.WriteUInt16((ushort)message.Type);
            message.Write(writer);
            writer.PatchInt32(0, writer.Length - 4);
            return writer.ToArray();
        }

        public static byte[] EncodeUdp(IMessage message, ulong token)
        {
            var writer = new PacketWriter(64);
            writer.WriteUInt64(token);
            writer.WriteUInt16((ushort)message.Type);
            message.Write(writer);
            return writer.ToArray();
        }

        /// <summary>Valida la cabecera TCP y devuelve la longitud del payload y el tipo.</summary>
        public static void ParseTcpHeader(byte[] header, out int payloadLength, out ushort type)
        {
            var reader = new PacketReader(header, 0, TcpHeaderSize);
            int length = reader.ReadInt32();
            if (length < 2 || length > ProtocolInfo.MaxTcpFrameBytes)
                throw new ProtocolException($"Longitud de trama inválida: {length}");

            payloadLength = length - 2;
            type = reader.ReadUInt16();
        }

        public static IMessage DecodePayload(ushort type, byte[] data, int offset, int count)
        {
            IMessage message = MessageRegistry.Create((MessageType)type);
            if (message == null)
                throw new ProtocolException($"Tipo de mensaje desconocido: {type}");

            message.Read(new PacketReader(data, offset, count));
            return message;
        }

        /// <summary>
        /// Lee una trama completa de forma bloqueante. Devuelve null si el flujo termina limpiamente
        /// entre tramas; lanza <see cref="EndOfStreamException"/> si termina a mitad de una.
        /// </summary>
        public static IMessage ReadTcpFrame(Stream stream, byte[] headerBuffer, out int frameBytes)
        {
            frameBytes = 0;
            if (!ReadExactly(stream, headerBuffer, TcpHeaderSize, allowCleanEof: true))
                return null;

            ParseTcpHeader(headerBuffer, out int payloadLength, out ushort type);
            var payload = new byte[payloadLength];
            ReadExactly(stream, payload, payloadLength, allowCleanEof: false);

            frameBytes = TcpHeaderSize + payloadLength;
            return DecodePayload(type, payload, 0, payloadLength);
        }

        public static bool TryDecodeUdp(byte[] data, int count, out ulong token, out IMessage message)
        {
            token = 0;
            message = null;
            if (count < UdpHeaderSize)
                return false;

            try
            {
                var reader = new PacketReader(data, 0, UdpHeaderSize);
                token = reader.ReadUInt64();
                ushort type = reader.ReadUInt16();
                message = DecodePayload(type, data, UdpHeaderSize, count - UdpHeaderSize);
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count, bool allowCleanEof)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0)
                {
                    if (read == 0 && allowCleanEof)
                        return false;
                    throw new EndOfStreamException("Conexión cerrada a mitad de un mensaje");
                }

                read += n;
            }

            return true;
        }
    }
}
