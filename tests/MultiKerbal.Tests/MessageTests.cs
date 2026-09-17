using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Tests;

public class MessageTests
{
    public static TheoryData<IMessage> Samples()
    {
        var data = new TheoryData<IMessage>();
        foreach (IMessage message in SampleMessages.All())
            data.Add(message);
        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Message_RoundTripsThroughTcpFrame(IMessage message)
    {
        byte[] frame = FrameCodec.EncodeTcp(message);
        using var stream = new MemoryStream(frame);

        IMessage decoded = FrameCodec.ReadTcpFrame(stream, new byte[FrameCodec.TcpHeaderSize], out int frameBytes);

        Assert.Equal(frame.Length, frameBytes);
        Assert.IsType(message.GetType(), decoded);
        Assert.Equal(frame, FrameCodec.EncodeTcp(decoded));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Message_RoundTripsThroughUdpDatagram(IMessage message)
    {
        byte[] datagram = FrameCodec.EncodeUdp(message, 0xDEADBEEFCAFEUL);

        Assert.True(FrameCodec.TryDecodeUdp(datagram, datagram.Length, out ulong token, out IMessage decoded));
        Assert.Equal(0xDEADBEEFCAFEUL, token);
        Assert.Equal(datagram, FrameCodec.EncodeUdp(decoded, token));
    }

    [Fact]
    public void EveryRegisteredType_HasASample()
    {
        HashSet<MessageType> sampled = SampleMessages.All().Select(m => m.Type).ToHashSet();

        foreach (MessageType type in MessageRegistry.RegisteredTypes)
            Assert.Contains(type, sampled);
    }

    [Fact]
    public void Registry_CreatesInstancesOfTheRequestedType()
    {
        foreach (MessageType type in MessageRegistry.RegisteredTypes)
            Assert.Equal(type, MessageRegistry.Create(type)!.Type);
    }

    [Fact]
    public void DefaultInstances_RoundTrip()
    {
        foreach (MessageType type in MessageRegistry.RegisteredTypes)
        {
            IMessage message = MessageRegistry.Create(type)!;
            byte[] frame = FrameCodec.EncodeTcp(message);
            using var stream = new MemoryStream(frame);
            IMessage decoded = FrameCodec.ReadTcpFrame(stream, new byte[FrameCodec.TcpHeaderSize], out _);
            Assert.Equal(frame, FrameCodec.EncodeTcp(decoded));
        }
    }
}

internal static class SampleMessages
{
    public static IEnumerable<IMessage> All()
    {
        yield return new HandshakeRequestMessage
        {
            ProtocolVersion = 1,
            PlayerName = "Jebediah",
            Password = "secreto",
            ModVersion = "0.1.0",
            GameVersion = "1.12.5",
        };
        yield return new HandshakeResponseMessage
        {
            ProtocolVersion = 1,
            Accepted = true,
            RejectReason = null,
            PlayerId = 3,
            UdpToken = ulong.MaxValue - 7,
            ServerName = "Servidor de pruebas",
            Motd = "¡Hola!",
        };
        yield return new DisconnectMessage { Reason = "Expulsado" };
        yield return new PingMessage { ClientTime = 123.456 };
        yield return new PongMessage { ClientTime = 123.456, ServerTime = 99999.25 };
        yield return new UdpBindMessage();
        yield return new UdpBindAckMessage();
        yield return new PlayerListMessage
        {
            Players =
            [
                new PlayerInfo { Id = 1, Name = "Jeb", ColorHue = 0.25f, Activity = PlayerActivity.Flight, Detail = "Kerbal X" },
                new PlayerInfo { Id = 2, Name = "Bill", ColorHue = 0.8f, Activity = PlayerActivity.Editor, Detail = null },
            ],
        };
        yield return new PlayerJoinedMessage
        {
            Player = new PlayerInfo { Id = 4, Name = "Val", ColorHue = 0.5f, Activity = PlayerActivity.SpaceCenter, Detail = "" },
        };
        yield return new PlayerLeftMessage { PlayerId = 4 };
        yield return new PlayerStatusMessage { PlayerId = 2, Activity = PlayerActivity.TrackingStation, Detail = "Mun" };
        yield return new ChatMessage { SenderId = 2, Text = "¿Nos acoplamos en órbita? 🚀" };
        yield return new TimeStateMessage
        {
            UniversalTime = 1_234_567.891,
            ServerTime = 42.5,
            Rate = 1000,
            Mode = WarpMode.Rails,
            LimitedBy = "Bill",
            LimiterAutoDenies = true,
        };
        yield return new WarpRequestMessage { Participating = true, Rate = 4, Mode = WarpMode.Physics, AcceptUpTo = 1000, AutoDeny = true };
        yield return new VesselProtoMessage
        {
            VesselId = Guid.Parse("6f1c6a8e-3b0e-4f5d-9d1a-2c7b8e9f0a11"),
            ControllerId = 2,
            OwnerName = "Valentina",
            Access = VesselAccess.Private,
            VesselName = "Kerbal X",
            StructureVersion = 3,
            Data = [0x1F, 0x8B, 1, 2, 3],
        };
        yield return new VesselUpdateMessage
        {
            VesselId = Guid.Parse("6f1c6a8e-3b0e-4f5d-9d1a-2c7b8e9f0a11"),
            UniversalTime = 12345.678,
            BodyIndex = 1,
            Situation = 32,
            Latitude = -0.0972,
            Longitude = -74.5577,
            Altitude = 80_000,
            Inclination = 5.5,
            Eccentricity = 0.001,
            SemiMajorAxis = 680_000,
            LongitudeOfAscendingNode = 90,
            ArgumentOfPeriapsis = 45,
            MeanAnomalyAtEpoch = 3.14,
            Epoch = 12345.6,
            RotationX = 0.1f,
            RotationY = 0.2f,
            RotationZ = 0.3f,
            RotationW = 0.927f,
        };
        yield return new VesselRemoveMessage { VesselId = Guid.NewGuid() };
        yield return new VesselControlMessage { VesselId = Guid.NewGuid(), ControllerId = 5 };
        yield return new VesselControlRequestMessage { VesselId = Guid.NewGuid(), Acquire = true };
        yield return new VesselOwnerMessage { VesselId = Guid.NewGuid(), OwnerName = "Bill", Access = VesselAccess.Public };
        yield return new VesselOwnerRequestMessage { VesselId = Guid.NewGuid(), OwnerName = string.Empty, Access = VesselAccess.Shared };
    }
}
