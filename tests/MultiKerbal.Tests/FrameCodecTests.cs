using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Tests;

public class FrameCodecTests
{
    [Fact]
    public void ConsecutiveFrames_AreReadInOrder_ThenCleanEof()
    {
        using var stream = new MemoryStream();
        stream.Write(FrameCodec.EncodeTcp(new ChatMessage { SenderId = 1, Text = "uno" }));
        stream.Write(FrameCodec.EncodeTcp(new PlayerLeftMessage { PlayerId = 7 }));
        stream.Position = 0;
        var header = new byte[FrameCodec.TcpHeaderSize];

        Assert.Equal("uno", Assert.IsType<ChatMessage>(FrameCodec.ReadTcpFrame(stream, header, out _)).Text);
        Assert.Equal(7, Assert.IsType<PlayerLeftMessage>(FrameCodec.ReadTcpFrame(stream, header, out _)).PlayerId);
        Assert.Null(FrameCodec.ReadTcpFrame(stream, header, out _));
    }

    [Fact]
    public void FramesArrivingOneByteAtATime_AreReassembled()
    {
        byte[] frame = FrameCodec.EncodeTcp(new ChatMessage { SenderId = 2, Text = "fragmentado" });
        using var stream = new TrickleStream(frame);

        IMessage message = FrameCodec.ReadTcpFrame(stream, new byte[FrameCodec.TcpHeaderSize], out int bytes);

        Assert.Equal("fragmentado", Assert.IsType<ChatMessage>(message).Text);
        Assert.Equal(frame.Length, bytes);
    }

    [Fact]
    public void StreamEndingMidFrame_ThrowsEndOfStream()
    {
        byte[] frame = FrameCodec.EncodeTcp(new ChatMessage { Text = "cortado" });
        using var stream = new MemoryStream(frame, 0, frame.Length - 3);

        Assert.Throws<EndOfStreamException>(() => FrameCodec.ReadTcpFrame(stream, new byte[FrameCodec.TcpHeaderSize], out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void InvalidFrameLength_Throws(int length)
    {
        var writer = new PacketWriter();
        writer.WriteInt32(length);
        writer.WriteUInt16((ushort)MessageType.Chat);

        Assert.Throws<ProtocolException>(() => FrameCodec.ParseTcpHeader(writer.ToArray(), out _, out _));
    }

    [Fact]
    public void UnknownMessageType_Throws()
    {
        Assert.Throws<ProtocolException>(() => FrameCodec.DecodePayload(60000, [], 0, 0));
    }

    [Fact]
    public void MalformedDatagrams_AreRejectedWithoutThrowing()
    {
        byte[] valid = FrameCodec.EncodeUdp(new PingMessage { ClientTime = 1 }, 5);
        byte[] unknownType = (byte[])valid.Clone();
        unknownType[8] = 0xFF;
        unknownType[9] = 0xFF;

        Assert.False(FrameCodec.TryDecodeUdp(valid, 5, out _, out _));
        Assert.False(FrameCodec.TryDecodeUdp(valid, valid.Length - 1, out _, out _));
        Assert.False(FrameCodec.TryDecodeUdp(unknownType, unknownType.Length, out _, out _));
        Assert.True(FrameCodec.TryDecodeUdp(valid, valid.Length, out ulong token, out _));
        Assert.Equal(5UL, token);
    }

    /// <summary>Simula una red lenta: cada lectura devuelve un solo byte.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count == 0)
                return 0;
            buffer[offset] = data[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
