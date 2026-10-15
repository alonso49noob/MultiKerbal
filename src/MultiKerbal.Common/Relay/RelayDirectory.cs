using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Relay
{
    /// <summary>
    /// Cliente de la lista pública del relay, para el mod. Bloquea hasta tener la respuesta, así que se llama
    /// desde un hilo aparte, nunca desde el hilo del juego.
    /// </summary>
    public static class RelayDirectory
    {
        /// <summary>
        /// Pide al relay la lista de servidores públicos (ya ordenada, saneada y como mucho 500).
        /// <paramref name="timeoutMs"/> es el plazo total para conectar y recibir la respuesta entera.
        /// </summary>
        /// <param name="relayAddress">"host" o "host:puerto" (por defecto, el puerto del relay).</param>
        /// <exception cref="IOException">Si no se puede conectar, el relay no contesta a tiempo o contesta algo ilegible.</exception>
        public static PublicServerEntry[] List(string relayAddress, int timeoutMs)
        {
            if (!RelayEndpoint.TryParse(relayAddress, ProtocolInfo.DefaultRelayPort, out string host, out int port))
            {
                throw new IOException(Lang.T(
                    "La dirección del relay no es válida: " + relayAddress,
                    "The relay address is not valid: " + relayAddress));
            }

            if (timeoutMs < 1)
                timeoutMs = 1;

            TcpClient tcp = null;
            Timer deadline = null;
            PublicServerEntry[] servers;
            try
            {
                IPAddress address = Resolve(host);
                tcp = new TcpClient(address.AddressFamily) { NoDelay = true };

                // El plazo vale para todo: pasado ese tiempo se corta la conexión y lo que esté esperando falla.
                TcpClient toClose = tcp;
                deadline = new Timer(_ => CloseQuietly(toClose), null, timeoutMs, Timeout.Infinite);

                tcp.Connect(address, port);
                NetworkStream stream = tcp.GetStream();
                byte[] request = FrameCodec.EncodeTcp(new RelayListRequestMessage { RelayVersion = ProtocolInfo.RelayVersion });
                stream.Write(request, 0, request.Length);

                IMessage reply = FrameCodec.ReadTcpFrame(stream, new byte[FrameCodec.TcpHeaderSize], out _);
                if (reply != null && !(reply is RelayListResponseMessage))
                    throw new ProtocolException("Respuesta inesperada del relay: " + reply.Type);

                servers = reply == null ? null : ((RelayListResponseMessage)reply).Servers;
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException
                                       || ex is ProtocolException || ex is InvalidOperationException)
            {
                // Incluye la lectura cortada por el plazo (se cierra el socket mientras se espera).
                throw Unreachable(relayAddress, ex);
            }
            finally
            {
                if (deadline != null)
                    deadline.Dispose();
                CloseQuietly(tcp);
            }

            if (servers == null)
            {
                // El relay corta sin contestar cuando no entiende la petición (otra versión del protocolo del relay).
                throw new IOException(Lang.T(
                    "El relay cerró la conexión sin dar la lista. Puede que use otra versión: actualiza MultiKerbal.",
                    "The relay closed the connection without sending the list. It may use another version: update MultiKerbal."));
            }

            return servers;
        }

        private static IOException Unreachable(string relayAddress, Exception inner) => new IOException(
            Lang.T(
                "No se pudo obtener la lista de servidores del relay " + relayAddress + ": " + inner.Message,
                "Could not get the server list from the relay " + relayAddress + ": " + inner.Message),
            inner);

        private static IPAddress Resolve(string host)
        {
            IPAddress parsed;
            if (IPAddress.TryParse(host, out parsed))
                return parsed;

            IPAddress[] addresses = Dns.GetHostAddresses(host);
            foreach (IPAddress address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    return address;
            }

            if (addresses.Length > 0)
                return addresses[0];
            throw new SocketException((int)SocketError.HostNotFound);
        }

        private static void CloseQuietly(IDisposable disposable)
        {
            try
            {
                if (disposable != null)
                    disposable.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
