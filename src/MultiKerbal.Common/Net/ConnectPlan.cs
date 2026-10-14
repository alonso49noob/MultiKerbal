using System;
using MultiKerbal.Common.Relay;

namespace MultiKerbal.Common.Net
{
    /// <summary>
    /// Cómo conectar: directo a una dirección, con un código (consulta al relay, intento directo y, si falla,
    /// entrar por el relay) o las dos cosas. Lo usa <see cref="NetClient.Connect(ConnectPlan)"/>.
    /// </summary>
    public sealed class ConnectPlan
    {
        /// <summary>Dirección directa del servidor; null si no se conoce (la puede rellenar la consulta).</summary>
        public string Host;

        public int Port;

        /// <summary>Relay para la consulta y para entrar por él; null si no se usa.</summary>
        public string RelayHost;

        public int RelayPort;

        /// <summary>Código del servidor, sin guion.</summary>
        public string JoinCode;

        /// <summary>Preguntar antes al relay por el servidor (nombre, versión y dirección directa).</summary>
        public bool LookupFirst;

        /// <summary>Plazo del intento directo.</summary>
        public int DirectTimeoutMs;

        /// <summary>Conexión por IP de siempre.</summary>
        public static ConnectPlan Direct(string host, int port, int timeoutMs = 5000) => new ConnectPlan
        {
            Host = host,
            Port = port,
            DirectTimeoutMs = timeoutMs,
        };

        /// <summary>
        /// Entrar con un código. Lanza <see cref="ArgumentException"/> si la dirección del relay o el código no son
        /// válidos: quien llama debe comprobarlos antes (<see cref="ServerCode.TryNormalize"/>).
        /// </summary>
        public static ConnectPlan ForCode(string relayAddress, string code)
        {
            if (!RelayEndpoint.TryParse(relayAddress, ProtocolInfo.DefaultRelayPort, out string relayHost, out int relayPort))
                throw new ArgumentException($"Dirección del relay inválida: {relayAddress}", nameof(relayAddress));
            if (!ServerCode.TryNormalize(code, out string normalized))
                throw new ArgumentException($"Código inválido: {code}", nameof(code));

            return new ConnectPlan
            {
                RelayHost = relayHost,
                RelayPort = relayPort,
                JoinCode = normalized,
                LookupFirst = true,
                DirectTimeoutMs = 3000,
            };
        }
    }
}
