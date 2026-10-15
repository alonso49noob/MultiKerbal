namespace MultiKerbal.Common
{
    public static class ProtocolInfo
    {
        /// <summary>Incrementar cada vez que cambie el formato de cualquier mensaje.</summary>
        public const int Version = 6;

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
        public const int MaxPurchasedParts = 10_000;
        public const int MaxFacilities = 100;
        public const int MaxScienceSubjects = 50_000;
        public const int MaxContracts = 5_000;
        public const int MaxKerbals = 5_000;

        /// <summary>Definición de un contrato o un kerbal (nodo de KSP en texto).</summary>
        public const int MaxNodeTextLength = 64 * 1024;

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

        /// <summary>Versión del protocolo entre servidor y relay (independiente de <see cref="Version"/>).</summary>
        public const int RelayVersion = 1;

        public const int DefaultRelayPort = 6760;
        public const string DefaultRelayAddress = "multikerbal.duckdns.org:6760";

        /// <summary>Tamaño máximo de un mensaje de control del relay.</summary>
        public const int MaxRelayMessageBytes = 64 * 1024;

        /// <summary>Máximo de servidores que devuelve la lista pública.</summary>
        public const int MaxListedServers = 500;

        /// <summary>Los nombres de la lista pública se cortan a esta longitud.</summary>
        public const int MaxListedNameLength = 40;

        /// <summary>Las versiones del mod que anuncian los servidores se cortan a esta longitud en el relay.</summary>
        public const int MaxListedVersionLength = 32;
    }
}
