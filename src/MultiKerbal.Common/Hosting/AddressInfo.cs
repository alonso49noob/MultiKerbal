using System.Net;
using System.Net.Sockets;

namespace MultiKerbal.Common.Hosting
{
    /// <summary>Qué tipo de dirección IPv4 es: de casa, de CGNAT o pública de verdad.</summary>
    public static class AddressInfo
    {
        /// <summary>Redes de casa (10/8, 172.16/12, 192.168/16) y autoasignadas (169.254/16).</summary>
        public static bool IsPrivate(IPAddress address)
        {
            if (!IsIPv4(address))
                return false;

            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        /// <summary>100.64/10: la que da un operador con CGNAT (y también Tailscale dentro de su red).</summary>
        public static bool IsCgnat(IPAddress address)
        {
            if (!IsIPv4(address))
                return false;

            byte[] b = address.GetAddressBytes();
            return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
        }

        /// <summary>Una dirección a la que se puede llegar desde Internet.</summary>
        public static bool IsPublic(IPAddress address)
        {
            if (!IsIPv4(address) || IsPrivate(address) || IsCgnat(address))
                return false;

            byte[] b = address.GetAddressBytes();
            return b[0] != 0 && b[0] != 127 && b[0] < 224;
        }

        private static bool IsIPv4(IPAddress address) =>
            address != null && address.AddressFamily == AddressFamily.InterNetwork;
    }
}
