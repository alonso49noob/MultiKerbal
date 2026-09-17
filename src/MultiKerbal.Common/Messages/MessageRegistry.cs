using System;
using System.Collections.Generic;

namespace MultiKerbal.Common.Messages
{
    public static class MessageRegistry
    {
        private static readonly Dictionary<MessageType, Func<IMessage>> Factories = new Dictionary<MessageType, Func<IMessage>>
        {
            { MessageType.HandshakeRequest, () => new HandshakeRequestMessage() },
            { MessageType.HandshakeResponse, () => new HandshakeResponseMessage() },
            { MessageType.Disconnect, () => new DisconnectMessage() },
            { MessageType.Ping, () => new PingMessage() },
            { MessageType.Pong, () => new PongMessage() },
            { MessageType.UdpBind, () => new UdpBindMessage() },
            { MessageType.UdpBindAck, () => new UdpBindAckMessage() },
            { MessageType.PlayerList, () => new PlayerListMessage() },
            { MessageType.PlayerJoined, () => new PlayerJoinedMessage() },
            { MessageType.PlayerLeft, () => new PlayerLeftMessage() },
            { MessageType.PlayerStatus, () => new PlayerStatusMessage() },
            { MessageType.Chat, () => new ChatMessage() },
            { MessageType.TimeState, () => new TimeStateMessage() },
            { MessageType.WarpRequest, () => new WarpRequestMessage() },
            { MessageType.VesselProto, () => new VesselProtoMessage() },
            { MessageType.VesselUpdate, () => new VesselUpdateMessage() },
            { MessageType.VesselRemove, () => new VesselRemoveMessage() },
            { MessageType.VesselControl, () => new VesselControlMessage() },
            { MessageType.VesselControlRequest, () => new VesselControlRequestMessage() },
            { MessageType.VesselOwner, () => new VesselOwnerMessage() },
            { MessageType.VesselOwnerRequest, () => new VesselOwnerRequestMessage() },
        };

        public static IEnumerable<MessageType> RegisteredTypes => Factories.Keys;

        /// <summary>Devuelve null si el tipo no está registrado.</summary>
        public static IMessage Create(MessageType type)
        {
            return Factories.TryGetValue(type, out Func<IMessage> factory) ? factory() : null;
        }
    }
}
