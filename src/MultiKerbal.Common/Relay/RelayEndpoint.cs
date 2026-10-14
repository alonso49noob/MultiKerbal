using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MultiKerbal.Common.Relay
{
    /// <summary>Lee direcciones del tipo "host", "host:puerto" y "[ipv6]:puerto".</summary>
    public static class RelayEndpoint
    {
        public static bool TryParse(string text, int defaultPort, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();
            string hostPart;
            string portPart = null;

            if (text[0] == '[')
            {
                int close = text.IndexOf(']');
                if (close < 0)
                    return false;

                hostPart = text.Substring(1, close - 1);
                string rest = text.Substring(close + 1);
                if (rest.Length > 0)
                {
                    if (rest[0] != ':')
                        return false;

                    portPart = rest.Substring(1);
                }
            }
            else
            {
                int colon = text.IndexOf(':');
                if (colon < 0)
                    hostPart = text;
                else if (text.IndexOf(':', colon + 1) >= 0)
                {
                    // Varios ':' sin corchetes: solo vale como IPv6 sin puerto.
                    if (!IsIPv6(text))
                        return false;

                    hostPart = text;
                }
                else
                {
                    hostPart = text.Substring(0, colon);
                    portPart = text.Substring(colon + 1);
                }
            }

            if (hostPart.Length == 0 || hostPart.IndexOf(' ') >= 0)
                return false;

            int parsedPort = defaultPort;
            if (portPart != null
                && !int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort))
                return false;

            if (parsedPort < 1 || parsedPort > 65535)
                return false;

            host = hostPart;
            port = parsedPort;
            return true;
        }

        private static bool IsIPv6(string text) =>
            IPAddress.TryParse(text, out IPAddress address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }
}
