using System;
using System.Collections.Generic;
using System.Security;
using System.Text;
using System.Xml;

namespace MultiKerbal.Common.Hosting
{
    /// <summary>Un servicio del router que sabe abrir puertos (WANIPConnection o WANPPPConnection).</summary>
    public sealed class UpnpService
    {
        public UpnpService(string serviceType, Uri controlUrl)
        {
            ServiceType = serviceType;
            ControlUrl = controlUrl;
        }

        public string ServiceType { get; }

        public Uri ControlUrl { get; }
    }

    /// <summary>
    /// Los mensajes de UPnP como texto: la búsqueda SSDP, la descripción del router y las llamadas SOAP.
    /// Sin red, para poder probarlo; la red está en <see cref="UpnpGateway"/>.
    /// </summary>
    public static class UpnpProtocol
    {
        public const string MulticastAddress = "239.255.255.250";
        public const int MulticastPort = 1900;

        /// <summary>Se pregunta por varios: cada router contesta a unos y no a otros.</summary>
        public static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
        };

        public static string BuildSearch(string searchTarget) =>
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {searchTarget}\r\n" +
            "\r\n";

        /// <summary>La cabecera LOCATION de una respuesta SSDP: dónde está la descripción del router.</summary>
        public static Uri ParseLocation(string response)
        {
            if (string.IsNullOrEmpty(response))
                return null;

            foreach (string rawLine in response.Split('\n'))
            {
                string line = rawLine.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0 || !line.Substring(0, colon).Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = line.Substring(colon + 1).Trim();
                return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttp ? uri : null;
            }

            return null;
        }

        /// <summary>Los servicios que abren puertos, del más moderno al más antiguo.</summary>
        public static List<UpnpService> ParseDescription(string xml, Uri location)
        {
            var services = new List<UpnpService>();
            XmlDocument doc = LoadXml(xml);
            if (doc == null)
                return services;

            Uri baseUri = location;
            string urlBase = FirstText(doc, "URLBase");
            if (!string.IsNullOrEmpty(urlBase) && Uri.TryCreate(urlBase, UriKind.Absolute, out Uri parsedBase))
                baseUri = parsedBase;

            XmlNodeList nodes = doc.SelectNodes("//*[local-name()='service']");
            if (nodes == null)
                return services;

            foreach (XmlNode node in nodes)
            {
                string type = ChildText(node, "serviceType");
                string control = ChildText(node, "controlURL");
                if (ServiceRank(type) < 0 || string.IsNullOrEmpty(control))
                    continue;

                if (Uri.TryCreate(baseUri, control, out Uri controlUrl))
                    services.Add(new UpnpService(type, controlUrl));
            }

            services.Sort((a, b) => ServiceRank(a.ServiceType).CompareTo(ServiceRank(b.ServiceType)));
            return services;
        }

        public static string BuildSoapBody(string serviceType, string action, IList<KeyValuePair<string, string>> arguments)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
            sb.Append("<s:Body>");
            sb.Append($"<u:{action} xmlns:u=\"{SecurityElement.Escape(serviceType)}\">");
            if (arguments != null)
            {
                // El orden importa: algunos routers leen los argumentos por posición.
                foreach (KeyValuePair<string, string> argument in arguments)
                    sb.Append($"<{argument.Key}>{SecurityElement.Escape(argument.Value ?? string.Empty)}</{argument.Key}>");
            }

            sb.Append($"</u:{action}>");
            sb.Append("</s:Body></s:Envelope>");
            return sb.ToString();
        }

        /// <summary>Los valores que devuelve una llamada SOAP, por nombre (p. ej. NewExternalIPAddress).</summary>
        public static Dictionary<string, string> ParseSoapResponse(string xml)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            XmlDocument doc = LoadXml(xml);
            XmlNode body = doc?.SelectSingleNode("//*[local-name()='Body']");
            XmlNode response = body == null ? null : FirstElement(body);
            if (response == null)
                return values;

            foreach (XmlNode child in response.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                    values[child.LocalName] = child.InnerText.Trim();
            }

            return values;
        }

        /// <summary>El error UPnP de una respuesta fallida (p. ej. 718 si el puerto ya lo usa otro PC).</summary>
        public static bool TryParseError(string xml, out int code, out string description)
        {
            code = 0;
            description = string.Empty;
            XmlDocument doc = LoadXml(xml);
            if (doc == null)
                return false;

            description = FirstText(doc, "errorDescription") ?? string.Empty;
            return int.TryParse(FirstText(doc, "errorCode"), out code);
        }

        private static int ServiceRank(string type)
        {
            if (string.IsNullOrEmpty(type))
                return -1;
            if (type.StartsWith("urn:schemas-upnp-org:service:WANIPConnection:2", StringComparison.Ordinal))
                return 0;
            if (type.StartsWith("urn:schemas-upnp-org:service:WANIPConnection:1", StringComparison.Ordinal))
                return 1;
            if (type.StartsWith("urn:schemas-upnp-org:service:WANPPPConnection:1", StringComparison.Ordinal))
                return 2;
            return -1;
        }

        private static XmlDocument LoadXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            try
            {
                // Sin DTD ni resolución externa: el XML viene de un aparato de la red.
                var doc = new XmlDocument { XmlResolver = null };
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                using (var reader = XmlReader.Create(new System.IO.StringReader(xml), settings))
                    doc.Load(reader);
                return doc;
            }
            catch (XmlException)
            {
                return null;
            }
        }

        private static string FirstText(XmlDocument doc, string localName) =>
            doc.SelectSingleNode($"//*[local-name()='{localName}']")?.InnerText.Trim();

        private static string ChildText(XmlNode node, string localName)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element && child.LocalName == localName)
                    return child.InnerText.Trim();
            }

            return null;
        }

        private static XmlNode FirstElement(XmlNode node)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                    return child;
            }

            return null;
        }
    }
}
