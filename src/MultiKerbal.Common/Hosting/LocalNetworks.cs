using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MultiKerbal.Common.Hosting
{
    public enum VirtualNetwork
    {
        None,
        Tailscale,
        ZeroTier,
        Radmin,
        Hamachi,
    }

    /// <summary>Una IP de este PC y, si es de una red virtual (Tailscale...), cuál.</summary>
    public sealed class LocalNetworkAddress
    {
        public LocalNetworkAddress(string interfaceName, IPAddress address, VirtualNetwork network)
        {
            InterfaceName = interfaceName;
            Address = address;
            Network = network;
        }

        public string InterfaceName { get; }

        public IPAddress Address { get; }

        public VirtualNetwork Network { get; }
    }

    /// <summary>Las IP de este PC que puede usar otro jugador para llegar al servidor.</summary>
    public static class LocalNetworks
    {
        /// <summary>Las IP de redes virtuales conocidas (Tailscale, ZeroTier, Radmin VPN, Hamachi).</summary>
        public static List<LocalNetworkAddress> FindVirtualNetworks()
        {
            var found = new List<LocalNetworkAddress>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    VirtualNetwork network = Classify(nic.Name, nic.Description);
                    if (network == VirtualNetwork.None)
                        continue;

                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                            found.Add(new LocalNetworkAddress(nic.Name, info.Address, network));
                    }
                }
            }
            catch (Exception)
            {
                // Sin lista de tarjetas no se puede saber; la ventana lo trata como "no encontrada".
            }

            return found;
        }

        /// <summary>Por el nombre de la tarjeta (Windows: "Tailscale", "ZeroTier One [...]"; Linux: tailscale0, zt...).</summary>
        public static VirtualNetwork Classify(string name, string description)
        {
            string text = ((name ?? string.Empty) + " " + (description ?? string.Empty)).ToLowerInvariant();
            if (text.Contains("tailscale"))
                return VirtualNetwork.Tailscale;
            if (text.Contains("zerotier") || (name != null && name.StartsWith("zt", StringComparison.Ordinal) && name.Length >= 10))
                return VirtualNetwork.ZeroTier;
            if (text.Contains("radmin"))
                return VirtualNetwork.Radmin;
            if (text.Contains("hamachi"))
                return VirtualNetwork.Hamachi;
            return VirtualNetwork.None;
        }

        /// <summary>La IP de este PC en la red de casa: la que usa el sistema para salir a Internet.</summary>
        public static IPAddress HomeAddress()
        {
            try
            {
                // Un UDP "conectado" solo elige la ruta, no manda nada.
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
