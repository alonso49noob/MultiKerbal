using MultiKerbal.Common.Messages;

namespace MultiKerbal.Common.Relay
{
    /// <summary>
    /// Texto de cada <see cref="RelayError"/> en el idioma de quien lo muestra (<see cref="Lang"/>). El relay no sabe el
    /// idioma del jugador ni del anfitrión: manda el código, y el mod o el servidor escriben el texto.
    /// </summary>
    public static class RelayErrorText
    {
        /// <summary>
        /// El texto del código; si el código es desconocido (un relay más nuevo) o no tiene texto propio, devuelve
        /// <paramref name="fallback"/>, el texto que mandó el relay (cadena vacía si es null).
        /// </summary>
        public static string Describe(RelayError error, string fallback)
        {
            switch (error)
            {
                case RelayError.VersionMismatch:
                    return Lang.T(
                        $"El relay usa otra versión del protocolo (esta copia de MultiKerbal usa la {ProtocolInfo.RelayVersion}). Actualiza MultiKerbal.",
                        $"The relay uses another protocol version (this copy of MultiKerbal uses version {ProtocolInfo.RelayVersion}). Update MultiKerbal.");
                case RelayError.NoServer:
                    return Lang.T("No hay ningún servidor encendido con ese código", "There is no server running with that code");
                case RelayError.ServerFull:
                    return Lang.T("El servidor está lleno", "The server is full");
                case RelayError.ServerNotAnswering:
                    return Lang.T("El servidor no responde a través del relay", "The server is not answering through the relay");
                case RelayError.TooManySessionsFromIp:
                    return Lang.T("Demasiadas conexiones desde tu IP a ese servidor. Espera un poco.", "Too many connections from your IP to that server. Wait a little.");
                case RelayError.Banned:
                    return Lang.T("Este servidor está bloqueado en el relay", "This server is blocked on the relay");
                case RelayError.TooManyServersFromIp:
                    return Lang.T("Demasiados servidores desde tu IP en el relay", "Too many servers from your IP on the relay");
                case RelayError.TooManyNewCodes:
                    return Lang.T(
                        "Demasiados códigos nuevos pedidos desde tu IP. Inténtalo más tarde.",
                        "Too many new codes requested from your IP. Try again later.");
                case RelayError.CodeNotSaved:
                    return Lang.T("El relay no pudo guardar tu código. Inténtalo más tarde.", "The relay could not save your code. Try again later.");
                case RelayError.InvalidGamePort:
                    return Lang.T("Puerto de juego inválido", "Invalid game port");
                default:
                    return fallback ?? string.Empty;
            }
        }
    }
}
