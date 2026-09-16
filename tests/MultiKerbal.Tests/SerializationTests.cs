using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Tests;

public class SerializationTests
{
    [Fact]
    public void Primitives_RoundTrip_AndBufferGrows()
    {
        var guid = Guid.NewGuid();
        var writer = new PacketWriter(4);
        writer.WriteByte(0xAB);
        writer.WriteBool(true);
        writer.WriteInt16(-12345);
        writer.WriteUInt16(54321);
        writer.WriteInt32(int.MinValue);
        writer.WriteUInt32(uint.MaxValue);
        writer.WriteInt64(long.MinValue);
        writer.WriteUInt64(ulong.MaxValue);
        writer.WriteSingle(3.14159f);
        writer.WriteDouble(-2.718281828459045);
        writer.WriteDouble(double.NaN);
        writer.WriteGuid(guid);

        var reader = new PacketReader(writer.ToArray());
        Assert.Equal(0xAB, reader.ReadByte());
        Assert.True(reader.ReadBool());
        Assert.Equal(-12345, reader.ReadInt16());
        Assert.Equal(54321, reader.ReadUInt16());
        Assert.Equal(int.MinValue, reader.ReadInt32());
        Assert.Equal(uint.MaxValue, reader.ReadUInt32());
        Assert.Equal(long.MinValue, reader.ReadInt64());
        Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
        Assert.Equal(3.14159f, reader.ReadSingle());
        Assert.Equal(-2.718281828459045, reader.ReadDouble());
        Assert.True(double.IsNaN(reader.ReadDouble()));
        Assert.Equal(guid, reader.ReadGuid());
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Jebediah Kerman")]
    [InlineData("¡Año 1, día 3! ñ 🚀 日本")]
    public void Strings_RoundTrip(string value)
    {
        var writer = new PacketWriter();
        writer.WriteString(value);

        Assert.Equal(value, new PacketReader(writer.ToArray()).ReadString());
    }

    [Fact]
    public void NullStringAndBytes_RoundTrip()
    {
        var writer = new PacketWriter();
        writer.WriteString(null);
        writer.WriteBytes(null);
        writer.WriteBytes([1, 2, 3]);

        var reader = new PacketReader(writer.ToArray());
        Assert.Null(reader.ReadString());
        Assert.Null(reader.ReadBytes());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBytes());
    }

    [Fact]
    public void LayoutIsLittleEndian()
    {
        var writer = new PacketWriter();
        writer.WriteUInt32(0x01020304);

        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, writer.ToArray());
    }

    [Fact]
    public void TruncatedData_Throws()
    {
        var writer = new PacketWriter();
        writer.WriteInt32(5);

        var reader = new PacketReader(writer.ToArray());
        Assert.Throws<ProtocolException>(() => reader.ReadInt64());
    }

    [Fact]
    public void StringLongerThanData_Throws()
    {
        var writer = new PacketWriter();
        writer.WriteInt32(1000);
        writer.WriteByte(65);

        Assert.Throws<ProtocolException>(() => new PacketReader(writer.ToArray()).ReadString());
    }

    [Fact]
    public void ReaderRespectsSliceBounds()
    {
        byte[] data = [9, 9, 1, 0, 0, 0, 9, 9];
        var reader = new PacketReader(data, 2, 4);

        Assert.Equal(1, reader.ReadInt32());
        Assert.Throws<ProtocolException>(() => reader.ReadByte());
    }

    [Fact]
    public void ReadCount_RejectsNegativeAndHuge()
    {
        var writer = new PacketWriter();
        writer.WriteInt32(-1);
        writer.WriteInt32(1_000_000);

        var reader = new PacketReader(writer.ToArray());
        Assert.Throws<ProtocolException>(() => reader.ReadCount(100));
        Assert.Throws<ProtocolException>(() => reader.ReadCount(100));
    }

    [Fact]
    public void PatchInt32_OverwritesInPlace()
    {
        var writer = new PacketWriter();
        writer.WriteInt32(0);
        writer.WriteByte(7);
        writer.PatchInt32(0, 42);

        var reader = new PacketReader(writer.ToArray());
        Assert.Equal(42, reader.ReadInt32());
        Assert.Equal(7, reader.ReadByte());
    }
}
