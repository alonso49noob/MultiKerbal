using System;
using System.Text;

namespace MultiKerbal.Common.Serialization
{
    /// <summary>Lector binario little-endian. Lanza <see cref="ProtocolException"/> si los datos están truncados o son inválidos.</summary>
    public sealed class PacketReader
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly byte[] _data;
        private readonly int _end;
        private int _position;

        public PacketReader(byte[] data) : this(data, 0, data?.Length ?? 0)
        {
        }

        public PacketReader(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            _data = data;
            _position = offset;
            _end = offset + count;
        }

        public int Remaining => _end - _position;

        public byte ReadByte()
        {
            Require(1);
            return _data[_position++];
        }

        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUInt16()
        {
            Require(2);
            ushort value = (ushort)(_data[_position] | (_data[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public short ReadInt16() => (short)ReadUInt16();

        public uint ReadUInt32()
        {
            Require(4);
            uint value = (uint)(_data[_position]
                                | (_data[_position + 1] << 8)
                                | (_data[_position + 2] << 16)
                                | (_data[_position + 3] << 24));
            _position += 4;
            return value;
        }

        public int ReadInt32() => (int)ReadUInt32();

        public ulong ReadUInt64()
        {
            Require(8);
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value |= (ulong)_data[_position + i] << (8 * i);
            _position += 8;
            return value;
        }

        public long ReadInt64() => (long)ReadUInt64();

        public float ReadSingle() => new SingleBits { Bits = ReadUInt32() }.Single;

        public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

        public string ReadString()
        {
            int length = ReadInt32();
            if (length == -1)
                return null;
            if (length < 0)
                throw new ProtocolException($"Longitud de texto inválida: {length}");

            Require(length);
            string value;
            try
            {
                value = Utf8.GetString(_data, _position, length);
            }
            catch (ArgumentException)
            {
                throw new ProtocolException("Texto UTF-8 inválido");
            }

            _position += length;
            return value;
        }

        public byte[] ReadBytes()
        {
            int length = ReadInt32();
            if (length == -1)
                return null;
            if (length < 0)
                throw new ProtocolException($"Longitud de bytes inválida: {length}");

            Require(length);
            var value = new byte[length];
            Buffer.BlockCopy(_data, _position, value, 0, length);
            _position += length;
            return value;
        }

        public Guid ReadGuid()
        {
            Require(16);
            var bytes = new byte[16];
            Buffer.BlockCopy(_data, _position, bytes, 0, 16);
            _position += 16;
            return new Guid(bytes);
        }

        /// <summary>Lee el número de elementos de una colección validando un máximo razonable.</summary>
        public int ReadCount(int max)
        {
            int count = ReadInt32();
            if (count < 0 || count > max)
                throw new ProtocolException($"Número de elementos inválido: {count}");
            return count;
        }

        private void Require(int count)
        {
            if (count > _end - _position)
                throw new ProtocolException("Mensaje truncado");
        }
    }
}
