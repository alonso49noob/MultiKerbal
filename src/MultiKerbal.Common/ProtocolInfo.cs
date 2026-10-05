namespace MultiKerbal.Common
{
    public static class ProtocolInfo
    {
        /// <summary>Incrementar cada vez que cambie el formato de cualquier mensaje.</summary>
        public const int Version = 5;

        public const int DefaultPort = 6750;

        public const int MaxTcpFrameBytes = 32 * 1024 * 1024;

        /// <summary>Tamaño máximo de una nave comprimida.</summary>
        public const int MaxVesselDataBytes = 16 * 1024 * 1024;

        /// <summary>Protege contra datos que se inflan desproporcionadamente al descomprimir.</summary>
        public const int MaxDecompressedBytes = 64 * 1024 * 1024;

        /// <summary>Por debajo del MTU típico de Internet para evitar fragmentación IP.</summary>
        public const int MaxUdpDatagramBytes = 1200;

        /// <summary>
        /// Sin UDP, lo no fiable (posiciones, mandos) va por TCP. Si hay más que esto por enviar, se descarta en vez de
        /// encolarlo: el siguiente estado llega enseguida y encolarlos todos solo acumularía retraso.
        /// </summary>
        public const int MaxQueuedUnreliableBytes = 32 * 1024;

        public const int MaxCrewPerVessel = 1000;
        public const int MaxTechNodes = 2000;

        /// <summary>Contraseña de la cuenta de cada jugador (no la del servidor).</summary>
        public const int MinAccountPasswordLength = 4;
        public const int MaxAccountPasswordLength = 128;

        public const int MinPlayerNameLength = 2;
        public const int MaxPlayerNameLength = 24;
        public const int MaxChatLength = 500;
        public const int MaxMods = 2000;
        public const int MaxModNameLength = 80;
        public const int MaxPlayers = 1024;

        public const double PingIntervalSeconds = 2.0;
        public const double TimeoutSeconds = 20.0;
        public const double HandshakeTimeoutSeconds = 10.0;
    }
}
