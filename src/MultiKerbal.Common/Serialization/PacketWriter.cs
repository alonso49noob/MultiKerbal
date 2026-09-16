using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiKerbal.Common.Serialization
{
    /// <summary>Escritor binario little-endian para el cuerpo de los mensajes.</summary>
    public sealed class PacketWriter
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private byte[] _data;
        private int _position;

        public PacketWriter(int initialCapacity = 256)
        {
            _data = new byte[Math.Max(16, initialCapacity)];
        }

        public int Length => _position;

        public byte[] ToArray()
        {
            var result = new byte[_position];
            Buffer.BlockCopy(_data, 0, result, 0, _position);
            return result;
        }

        public void WriteByte(byte value)
        {
            Reserve(1);
            _data[_position++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort value)
        {
            Reserve(2);
            _data[_position++] = (byte)value;
            _data[_position++] = (byte)(value >> 8);
        }

        public void WriteInt16(short value) => WriteUInt16((ushort)value);

        public void WriteUInt32(uint value)
        {
            Reserve(4);
            _data[_position++] = (byte)value;
            _data[_position++] = (byte)(value >> 8);
            _data[_position++] = (byte)(value >> 16);
            _data[_position++] = (byte)(value >> 24);
        }

        public void WriteInt32(int value) => WriteUInt32((uint)value);

        public void WriteUInt64(ulong value)
        {
            Reserve(8);
            for (int i = 0; i < 8; i++)
                _data[_position++] = (byte)(value >> (8 * i));
        }

        public void WriteInt64(long value) => WriteUInt64((ulong)value);

        public void WriteSingle(float value) => WriteUInt32(new SingleBits { Single = value }.Bits);

        public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

        /// <summary>Longitud en bytes (int32, -1 para null) seguida de UTF-8.</summary>
        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt32(-1);
                return;
            }

            int byteCount = Utf8.GetByteCount(value);
            WriteInt32(byteCount);
            Reserve(byteCount);
            _position += Utf8.GetBytes(value, 0, value.Length, _data, _position);
        }

        /// <summary>Longitud (int32, -1 para null) seguida de los bytes.</summary>
        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                WriteInt32(-1);
                return;
            }

            WriteInt32(value.Length);
            WriteRaw(value, 0, value.Length);
        }

        public void WriteRaw(byte[] source, int offset, int count)
        {
            Reserve(count);
            Buffer.BlockCopy(source, offset, _data, _position, count);
            _position += count;
        }

        public void WriteGuid(Guid value) => WriteRaw(value.ToByteArray(), 0, 16);

        /// <summary>Sobrescribe un int32 ya escrito (p. ej. la longitud de una trama).</summary>
        public void PatchInt32(int position, int value)
        {
            if (position < 0 || position + 4 > _position)
                throw new ArgumentOutOfRangeException(nameof(position));

            _data[position] = (byte)value;
            _data[position + 1] = (byte)(value >> 8);
            _data[position + 2] = (byte)(value >> 16);
            _data[position + 3] = (byte)(value >> 24);
        }

        private void Reserve(int count)
        {
            int required = _position + count;
            if (required <= _data.Length)
                return;

            int newSize = _data.Length * 2;
            while (newSize < required)
                newSize *= 2;
            Array.Resize(ref _data, newSize);
        }
    }

    /// <summary>BitConverter.SingleToInt32Bits no existe en net472.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct SingleBits
    {
        [FieldOffset(0)] public float Single;
        [FieldOffset(0)] public uint Bits;
    }
}
