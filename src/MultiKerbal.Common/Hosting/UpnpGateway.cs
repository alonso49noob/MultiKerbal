using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

// WebRequest está obsoleto en .NET moderno, pero es lo que trae el Mono de KSP.
#pragma warning disable SYSLIB0014

namespace MultiKerbal.Common.Hosting
{
    /// <summary>Error que devuelve el router a una llamada UPnP.</summary>
    public sealed class UpnpException : Exception
    {
        public UpnpException(int code, string description)
            : base(string.IsNullOrEmpty(description) ? $"UPnP {code}" : $"UPnP {code}: {description}")
        {
            Code = code;
        }

        /// <summary>718: el puerto ya está abierto hacia otro PC. 606: el router no deja cambiar nada.</summary>
        public int Code { get; }
    }

    /// <summary>
    /// Un router con UPnP. Todas las llamadas bloquean (red), así que se usan desde un hilo aparte.
    /// </summary>
    public sealed class UpnpGateway
    {
        public const int ConflictInMappingEntry = 718;
        public const int NoSuchEntry = 714;

        private const int HttpTimeoutMs = 4000;

        private UpnpGateway(UpnpService service, IPAddress localAddress, IPAddress externalAddress)
        {
            Service = service;
            LocalAddress = localAddress;
            ExternalAddress = externalAddress;
        }

        public UpnpService Service { get; }

        /// <summary>La IP de este PC en la red del router: hacia ella se abre el puerto.</summary>
        public IPAddress LocalAddress { get; }

        /// <summary>La IP que el router dice tener hacia Internet. Si es privada o de CGNAT, abrir puertos no basta.</summary>
        public IPAddress ExternalAddress { get; }

        /// <summary>Busca el router por SSDP y devuelve el primero que contesta a las llamadas, o null.</summary>
        public static UpnpGateway Discover(int timeoutMs)
        {
            foreach (Uri location in Search(timeoutMs))
            {
                List<UpnpService> services;
                try
                {
                    services = UpnpProtocol.ParseDescription(HttpGet(location), location);
                }
                catch (Exception)
                {
                    continue;
                }

                IPAddress local = LocalAddressTowards(location);
                foreach (UpnpService service in services)
                {
                    try
                    {
                        Dictionary<string, string> result = Call(service, "GetExternalIPAddress", null);
                        result.TryGetValue("NewExternalIPAddress", out string text);
                        IPAddress.TryParse(text ?? string.Empty, out IPAddress external);
                        return new UpnpGateway(service, local, external);
                    }
                    catch (Exception)
                    {
                        // Ese servicio no está conectado (p. ej. PPP sin usar); se prueba el siguiente.
                    }
                }
            }

            return null;
        }

        public void AddPortMapping(int port, string protocol, string description)
        {
            Call(Service, "AddPortMapping", new List<KeyValuePair<string, string>>
            {
                Arg("NewRemoteHost", string.Empty),
                Arg("NewExternalPort", port.ToString()),
                Arg("NewProtocol", protocol),
                Arg("NewInternalPort", port.ToString()),
                Arg("NewInternalClient", LocalAddress.ToString()),
                Arg("NewEnabled", "1"),
                Arg("NewPortMappingDescription", description),
                // 0: sin caducidad (en los routers UPnP 2 significa "lo máximo", una semana).
                Arg("NewLeaseDuration", "0"),
            });
        }

        public void DeletePortMapping(int port, string protocol)
        {
            Call(Service, "DeletePortMapping", new List<KeyValuePair<string, string>>
            {
                Arg("NewRemoteHost", string.Empty),
                Arg("NewExternalPort", port.ToString()),
                Arg("NewProtocol", protocol),
            });
        }

        /// <summary>A qué PC apunta ya ese puerto, o null si no está abierto.</summary>
        public IPAddress GetMappingClient(int port, string protocol)
        {
            try
            {
                Dictionary<string, string> result = Call(Service, "GetSpecificPortMappingEntry", new List<KeyValuePair<string, string>>
                {
                    Arg("NewRemoteHost", string.Empty),
                    Arg("NewExternalPort", port.ToString()),
                    Arg("NewProtocol", protocol),
                });
                result.TryGetValue("NewInternalClient", out string client);
                return IPAddress.TryParse(client ?? string.Empty, out IPAddress address) ? address : null;
            }
            catch (UpnpException ex) when (ex.Code == NoSuchEntry || ex.Code == 716)
            {
                return null;
            }
        }

        private static KeyValuePair<string, string> Arg(string name, string value) => new KeyValuePair<string, string>(name, value);

        /// <summary>
        /// Manda la búsqueda desde cada dirección de este PC: con varias tarjetas de red (o Tailscale, VirtualBox...)
        /// el multicast podría salir por la que no es.
        /// </summary>
        private static List<Uri> Search(int timeoutMs)
        {
            var locations = new List<Uri>();
            var sockets = new List<Socket>();
            try
            {
                var targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse(UpnpProtocol.MulticastAddress), UpnpProtocol.MulticastPort) };
                List<IPAddress> locals = LocalIPv4Addresses(out List<IPAddress> gateways);

                // Algunos routers no contestan al multicast pero sí si se les pregunta directamente.
                foreach (IPAddress gateway in gateways)
                    targets.Add(new IPEndPoint(gateway, UpnpProtocol.MulticastPort));

                foreach (IPAddress local in locals)
                {
                    try
                    {
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        sockets.Add(socket);
                        socket.Bind(new IPEndPoint(local, 0));
                        if (!local.Equals(IPAddress.Any))
                            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());

                        foreach (IPEndPoint target in targets)
                        {
                            foreach (string searchTarget in UpnpProtocol.SearchTargets)
                                socket.SendTo(Encoding.ASCII.GetBytes(UpnpProtocol.BuildSearch(searchTarget)), target);
                        }
                    }
                    catch (SocketException)
                    {
                        // Esa tarjeta no deja mandar multicast; se sigue con las demás.
                    }
                }

                var buffer = new byte[4096];
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMs && sockets.Count > 0)
                {
                    var readable = new List<Socket>(sockets);
                    int remainingUs = (int)Math.Max(1, (timeoutMs - clock.ElapsedMilliseconds) * 1000);
                    Socket.Select(readable, null, null, remainingUs);
                    foreach (Socket socket in readable)
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int received;
                        try
                        {
                            received = socket.ReceiveFrom(buffer, ref from);
                        }
                        catch (SocketException)
                        {
                            continue;
                        }

                        Uri location = UpnpProtocol.ParseLocation(Encoding.ASCII.GetString(buffer, 0, received));
                        if (location != null && !locations.Contains(location))
                            locations.Add(location);
                    }
                }
            }
            finally
            {
                foreach (Socket socket in sockets)
                    socket.Close();
            }

            return locations;
        }

        private static List<IPAddress> LocalIPv4Addresses(out List<IPAddress> gateways)
        {
            var addresses = new List<IPAddress>();
            gateways = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    IPInterfaceProperties properties = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        if (info.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(info.Address))
                            addresses.Add(info.Address);
                    }

                    foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any) && !gateways.Contains(gateway.Address))
                            gateways.Add(gateway.Address);
                    }
                }
            }
            catch (Exception)
            {
                // Algunas plataformas no saben listar las tarjetas: se busca por la que elija el sistema.
            }

            if (addresses.Count == 0)
                addresses.Add(IPAddress.Any);
            return addresses;
        }

        /// <summary>Qué IP de este PC usa el sistema para hablar con el router (un UDP "conectado" no manda nada).</summary>
        private static IPAddress LocalAddressTowards(Uri location)
        {
            try
            {
                IPAddress host = IPAddress.TryParse(location.Host, out IPAddress parsed) ? parsed : Dns.GetHostAddresses(location.Host)[0];
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(host, location.Port));
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            }
            catch (Exception)
            {
                return IPAddress.Any;
            }
        }

        private static string HttpGet(Uri uri)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            Prepare(request);
            using (WebResponse response = request.GetResponse())
                return ReadBody(response);
        }

        private static Dictionary<string, string> Call(UpnpService service, string action, IList<KeyValuePair<string, string>> arguments)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(UpnpProtocol.BuildSoapBody(service.ServiceType, action, arguments));
            var request = (HttpWebRequest)WebRequest.Create(service.ControlUrl);
            request.Method = "POST";
            request.ContentType = "text/xml; charset=\"utf-8\"";
            request.Headers.Add("SOAPAction", $"\"{service.ServiceType}#{action}\"");
            request.ContentLength = body.Length;
            Prepare(request);

            try
            {
                using (Stream stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);
                using (WebResponse response = request.GetResponse())
                    return UpnpProtocol.ParseSoapResponse(ReadBody(response));
            }
            catch (WebException ex) when (ex.Response != null)
            {
                string text;
                using (ex.Response)
                    text = ReadBody(ex.Response);
                if (UpnpProtocol.TryParseError(text, out int code, out string description))
                    throw new UpnpException(code, description);
                throw;
            }
        }

        private static void Prepare(HttpWebRequest request)
        {
            request.Timeout = HttpTimeoutMs;
            request.ReadWriteTimeout = HttpTimeoutMs;
            request.KeepAlive = false;
            // El router está en casa: nunca por un proxy. Y sin "Expect: 100-continue", que algunos no entienden.
            request.Proxy = null;
            request.ServicePoint.Expect100Continue = false;
        }

        private static string ReadBody(WebResponse response)
        {
            using (Stream stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
