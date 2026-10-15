using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Relay
{
    /// <summary>
    /// Cliente bloqueante de la consulta de estado y del control local del servidor, en su puerto de juego. Lo usa la
    /// ventana Hospedar del mod, desde un hilo suyo: nada de async, y todo con plazo.
    /// </summary>
    public static class ServerStatusQuery
    {
        /// <summary>Límite de los textos que vienen del servidor (una consulta puede apuntar a cualquier dirección).</summary>
        private const int MaxErrorLength = 200;

        /// <summary>
        /// Pregunta el estado a un servidor. Devuelve null si no contesta a tiempo o cierra sin contestar.
        /// <paramref name="portOpen"/> es verdadero si la TCP llegó a abrirse: así se distingue "no hay servidor"
        /// (puerto cerrado) de "hay algo que no entiende la consulta", como un servidor de una versión anterior, que
        /// cierra la conexión al recibir un mensaje que no conoce.
        /// </summary>
        public static ServerStatusResponseMessage Query(string host, int port, int timeoutMs, out bool portOpen)
        {
            ServerStatusResponseMessage response = Exchange(host, port, timeoutMs, new ServerStatusRequestMessage(), out portOpen);
            if (response == null)
                return null;

            // Los textos se limpian aquí: se van a mostrar y a escribir en el registro.
            response.Name = ListedName.Sanitize(response.Name);
            response.ModVersion = ListedName.Sanitize(response.ModVersion);
            response.RelayError = ListedName.Sanitize(response.RelayError, MaxErrorLength);
            return response;
        }

        /// <summary>
        /// Manda una orden al servidor. Solo la acepta desde este mismo equipo, y en ese caso contesta con su estado ya
        /// actualizado: verdadero significa que el servidor la recibió y la aplicó. Falso si no hay servidor, no contesta
        /// a tiempo o la ignora (por ejemplo, porque no se hizo desde su propio equipo).
        /// </summary>
        public static bool SendControl(string host, int port, ServerControlAction action, int timeoutMs)
        {
            bool portOpen;
            return Exchange(host, port, timeoutMs, new ServerControlMessage { Action = action }, out portOpen) != null;
        }

        /// <summary>Abre la conexión, manda el mensaje y espera la respuesta de estado. Cierra siempre el socket.</summary>
        private static ServerStatusResponseMessage Exchange(string host, int port, int timeoutMs, IMessage request, out bool portOpen)
        {
            portOpen = false;
            TcpClient tcp = null;
            try
            {
                var clock = Stopwatch.StartNew();
                IPAddress address = Resolve(host);
                tcp = new TcpClient(address.AddressFamily) { NoDelay = true };

                IAsyncResult connect = tcp.BeginConnect(address, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
                    return null;
                tcp.EndConnect(connect);
                portOpen = true;

                NetworkStream stream = tcp.GetStream();
                byte[] frame = FrameCodec.EncodeTcp(request);
                stream.WriteTimeout = Remaining(clock, timeoutMs);
                stream.Write(frame, 0, frame.Length);

                var header = new byte[FrameCodec.TcpHeaderSize];
                while (true)
                {
                    stream.ReadTimeout = Remaining(clock, timeoutMs);
                    int frameBytes;
                    IMessage message = FrameCodec.ReadTcpFrame(stream, header, out frameBytes);
                    if (message == null)
                        return null; // cerró sin contestar

                    ServerStatusResponseMessage response = message as ServerStatusResponseMessage;
                    if (response != null)
                        return response;

                    if (message is DisconnectMessage)
                        return null; // la ignoró y cierra
                }
            }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                return null;
            }
            finally
            {
                if (tcp != null)
                {
                    try
                    {
                        tcp.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        /// <summary>Lo que queda del plazo total, con un mínimo de 1 ms (con 0 el socket esperaría sin límite).</summary>
        private static int Remaining(Stopwatch clock, int timeoutMs)
        {
            long left = timeoutMs - clock.ElapsedMilliseconds;
            if (left <= 0)
                throw new TimeoutException();
            return (int)left;
        }

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

        private static bool IsNetworkError(Exception ex) =>
            ex is SocketException || ex is IOException || ex is TimeoutException || ex is ObjectDisposedException
            || ex is ProtocolException || ex is ArgumentException;
    }
}
