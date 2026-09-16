namespace MultiKerbal.Common
{
    public static class ProtocolInfo
    {
        /// <summary>Incrementar cada vez que cambie el formato de cualquier mensaje.</summary>
        public const int Version = 3;

        public const int DefaultPort = 6750;

        public const int MaxTcpFrameBytes = 32 * 1024 * 1024;

        /// <summary>Tamaño máximo de una nave comprimida.</summary>
        public const int MaxVesselDataBytes = 16 * 1024 * 1024;

        /// <summary>Protege contra datos que se inflan desproporcionadamente al descomprimir.</summary>
        public const int MaxDecompressedBytes = 64 * 1024 * 1024;

        /// <summary>Por debajo del MTU típico de Internet para evitar fragmentación IP.</summary>
        public const int MaxUdpDatagramBytes = 1200;

        public const int MinPlayerNameLength = 2;
        public const int MaxPlayerNameLength = 24;
        public const int MaxChatLength = 500;
        public const int MaxPlayers = 1024;

        public const double PingIntervalSeconds = 2.0;
        public const double TimeoutSeconds = 20.0;
        public const double HandshakeTimeoutSeconds = 10.0;
    }
}
