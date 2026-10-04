using System;
using System.IO;
using System.Net;
using System.Text;

#pragma warning disable SYSLIB0014

namespace MultiKerbal.Common.Hosting
{
    public enum PortForwardOutcome
    {
        /// <summary>El puerto está abierto en el router hacia este PC, por TCP y UDP.</summary>
        Opened,

        /// <summary>El puerto se cerró (o ya no estaba abierto).</summary>
        Closed,

        /// <summary>Ningún router contestó por UPnP: está desactivado o el router no lo tiene.</summary>
        NoRouter,

        /// <summary>El puerto ya está abierto hacia otro PC de la casa.</summary>
        Conflict,

        /// <summary>El router contestó pero no quiso (UPnP en solo lectura, error raro...).</summary>
        Refused,
    }

    public enum NatVerdict
    {
        /// <summary>No se pudo saber (falta la IP del router o la pública).</summary>
        Unknown,

        /// <summary>El router tiene la IP pública: abrir el puerto basta.</summary>
        Direct,

        /// <summary>CGNAT o dos routers seguidos: desde Internet no se llega aunque el puerto esté abierto.</summary>
        Blocked,
    }

    public sealed class PortForwardResult
    {
        public PortForwardOutcome Outcome;

        public int Port;

        /// <summary>Error del router, o el PC que ya tiene el puerto si es un conflicto.</summary>
        public string Detail = string.Empty;

        public IPAddress LocalAddress;

        /// <summary>La IP que dice tener el router hacia Internet.</summary>
        public IPAddress RouterAddress;

        /// <summary>La IP con la que se nos ve desde Internet.</summary>
        public IPAddress PublicAddress;

        public NatVerdict Nat;
    }

    /// <summary>Abre y cierra el puerto del servidor en el router de casa, y comprueba si con eso basta.</summary>
    public static class PortForwarding
    {
        public const string MappingDescription = "MultiKerbal";

        /// <summary>Servicio que devuelve en texto plano la IP con la que se le llama. Solo se usa al pulsar el botón.</summary>
        public const string PublicIpService = "http://api.ipify.org";

        private const int DiscoveryTimeoutMs = 3000;
        private static readonly string[] Protocols = { "TCP", "UDP" };

        /// <summary>Bloquea varios segundos: llamar desde un hilo aparte.</summary>
        public static PortForwardResult Open(int port)
        {
            var result = new PortForwardResult { Port = port };
            UpnpGateway gateway = UpnpGateway.Discover(DiscoveryTimeoutMs);
            if (gateway == null)
            {
                result.Outcome = PortForwardOutcome.NoRouter;
            }
            else
            {
                result.LocalAddress = gateway.LocalAddress;
                result.RouterAddress = gateway.ExternalAddress;
                result.Outcome = PortForwardOutcome.Opened;
                foreach (string protocol in Protocols)
                {
                    if (!TryOpen(gateway, port, protocol, result))
                        break;
                }
            }

            result.PublicAddress = FetchPublicAddress();
            result.Nat = Evaluate(result.RouterAddress, result.PublicAddress);
            return result;
        }

        /// <summary>Bloquea varios segundos: llamar desde un hilo aparte.</summary>
        public static PortForwardResult Close(int port)
        {
            var result = new PortForwardResult { Port = port };
            UpnpGateway gateway = UpnpGateway.Discover(DiscoveryTimeoutMs);
            if (gateway == null)
            {
                result.Outcome = PortForwardOutcome.NoRouter;
                return result;
            }

            result.LocalAddress = gateway.LocalAddress;
            result.RouterAddress = gateway.ExternalAddress;
            result.Outcome = PortForwardOutcome.Closed;
            foreach (string protocol in Protocols)
            {
                try
                {
                    // Solo se cierra si apunta a este PC: el puerto podría ser de otro de la casa.
                    IPAddress client = gateway.GetMappingClient(port, protocol);
                    if (client != null && client.Equals(gateway.LocalAddress))
                        gateway.DeletePortMapping(port, protocol);
                }
                catch (Exception ex)
                {
                    result.Outcome = PortForwardOutcome.Refused;
                    result.Detail = ex.Message;
                }
            }

            return result;
        }

        public static NatVerdict Evaluate(IPAddress routerAddress, IPAddress publicAddress)
        {
            if (routerAddress != null && !routerAddress.Equals(IPAddress.Any) && !AddressInfo.IsPublic(routerAddress))
                return NatVerdict.Blocked;

            if (routerAddress == null || publicAddress == null || routerAddress.Equals(IPAddress.Any))
                return NatVerdict.Unknown;

            return routerAddress.Equals(publicAddress) ? NatVerdict.Direct : NatVerdict.Blocked;
        }

        /// <summary>La IP pública según <see cref="PublicIpService"/>, o null si no contesta.</summary>
        public static IPAddress FetchPublicAddress()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(PublicIpService);
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.KeepAlive = false;
                using (WebResponse response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.ASCII))
                {
                    string text = reader.ReadToEnd().Trim();
                    return IPAddress.TryParse(text, out IPAddress address) ? address : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryOpen(UpnpGateway gateway, int port, string protocol, PortForwardResult result)
        {
            try
            {
                gateway.AddPortMapping(port, protocol, MappingDescription);
                return true;
            }
            catch (UpnpException ex) when (ex.Code == UpnpGateway.ConflictInMappingEntry)
            {
                IPAddress owner = SafeMappingClient(gateway, port, protocol);
                if (owner != null && owner.Equals(gateway.LocalAddress))
                    return true;

                result.Outcome = PortForwardOutcome.Conflict;
                result.Detail = owner?.ToString() ?? string.Empty;
                return false;
            }
            catch (Exception ex)
            {
                result.Outcome = PortForwardOutcome.Refused;
                result.Detail = ex.Message;
                return false;
            }
        }

        private static IPAddress SafeMappingClient(UpnpGateway gateway, int port, string protocol)
        {
            try
            {
                return gateway.GetMappingClient(port, protocol);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
