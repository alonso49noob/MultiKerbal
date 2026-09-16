namespace MultiKerbal.Common.Messages
{
    /// <summary>Identificador de cada mensaje en el cable. No reutilizar números retirados.</summary>
    public enum MessageType : ushort
    {
        // Conexión
        HandshakeRequest = 1,
        HandshakeResponse = 2,
        Disconnect = 3,
        Ping = 4,
        Pong = 5,
        UdpBind = 6,
        UdpBindAck = 7,

        // Jugadores
        PlayerList = 20,
        PlayerJoined = 21,
        PlayerLeft = 22,
        PlayerStatus = 23,

        // Chat
        Chat = 40,

        // Tiempo
        TimeState = 50,
        WarpRequest = 51,

        // Naves
        VesselProto = 60,
        VesselUpdate = 61,
        VesselRemove = 62,
        VesselOwnership = 63,
        VesselOwnershipRequest = 64,
    }
}
