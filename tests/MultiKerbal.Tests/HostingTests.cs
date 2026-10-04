using System.Net;
using MultiKerbal.Common.Hosting;

namespace MultiKerbal.Tests;

public class HostingTests
{
    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.100.13", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("85.10.20.30", false)]
    public void PrivateAddresses(string address, bool expected) =>
        Assert.Equal(expected, AddressInfo.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.254", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("85.10.20.30", true)]
    public void PublicAddresses(string address, bool expected) =>
        Assert.Equal(expected, AddressInfo.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void ParsesSsdpLocationCaseInsensitive()
    {
        string response = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nlocation: http://192.168.1.1:5000/rootDesc.xml\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
        Assert.Equal(new Uri("http://192.168.1.1:5000/rootDesc.xml"), UpnpProtocol.ParseLocation(response));
    }

    [Fact]
    public void IgnoresSsdpWithoutLocation()
    {
        Assert.Null(UpnpProtocol.ParseLocation("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n"));
        Assert.Null(UpnpProtocol.ParseLocation(""));
    }

    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                <controlURL>/ctl/L3F</controlURL>
              </service>
            </serviceList>
            <deviceList>
              <device>
                <deviceList>
                  <device>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>
                        <controlURL>/ctl/PPP</controlURL>
                      </service>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                        <controlURL>/ctl/IPConn</controlURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </deviceList>
          </device>
        </root>
        """;

    [Fact]
    public void FindsPortMappingServicesInPreferenceOrder()
    {
        List<UpnpService> services = UpnpProtocol.ParseDescription(Description, new Uri("http://192.168.1.1:5000/rootDesc.xml"));

        Assert.Equal(2, services.Count);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", services[0].ServiceType);
        Assert.Equal(new Uri("http://192.168.1.1:5000/ctl/IPConn"), services[0].ControlUrl);
        Assert.Equal("urn:schemas-upnp-org:service:WANPPPConnection:1", services[1].ServiceType);
    }

    [Fact]
    public void ControlUrlUsesUrlBaseWhenPresent()
    {
        string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <URLBase>http://192.168.0.1:49152/</URLBase>
              <device><serviceList><service>
                <serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType>
                <controlURL>upnp/control/WANIPConn1</controlURL>
              </service></serviceList></device>
            </root>
            """;

        List<UpnpService> services = UpnpProtocol.ParseDescription(xml, new Uri("http://192.168.0.1:1900/desc.xml"));

        Assert.Single(services);
        Assert.Equal(new Uri("http://192.168.0.1:49152/upnp/control/WANIPConn1"), services[0].ControlUrl);
    }

    [Fact]
    public void BrokenDescriptionGivesNoServices()
    {
        Assert.Empty(UpnpProtocol.ParseDescription("<root><device>", new Uri("http://192.168.1.1/")));
        Assert.Empty(UpnpProtocol.ParseDescription("", new Uri("http://192.168.1.1/")));
    }

    [Fact]
    public void SoapBodyKeepsArgumentOrderAndEscapes()
    {
        string body = UpnpProtocol.BuildSoapBody("urn:schemas-upnp-org:service:WANIPConnection:1", "AddPortMapping",
            new List<KeyValuePair<string, string>>
            {
                new("NewExternalPort", "6750"),
                new("NewProtocol", "UDP"),
                new("NewPortMappingDescription", "A & B"),
            });

        Assert.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", body);
        Assert.True(body.IndexOf("NewExternalPort", StringComparison.Ordinal) < body.IndexOf("NewProtocol", StringComparison.Ordinal));
        Assert.Contains("<NewPortMappingDescription>A &amp; B</NewPortMappingDescription>", body);
    }

    [Fact]
    public void ParsesSoapResponseValues()
    {
        string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
                  <NewExternalIPAddress> 85.10.20.30 </NewExternalIPAddress>
                </u:GetExternalIPAddressResponse>
              </s:Body>
            </s:Envelope>
            """;

        Assert.Equal("85.10.20.30", UpnpProtocol.ParseSoapResponse(xml)["NewExternalIPAddress"]);
    }

    [Fact]
    public void ParsesUpnpError()
    {
        string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>
                <detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>718</errorCode><errorDescription>ConflictInMappingEntry</errorDescription>
                </UPnPError></detail>
              </s:Fault></s:Body>
            </s:Envelope>
            """;

        Assert.True(UpnpProtocol.TryParseError(xml, out int code, out string description));
        Assert.Equal(718, code);
        Assert.Equal("ConflictInMappingEntry", description);
        Assert.False(UpnpProtocol.TryParseError("<html>500</html>", out _, out _));
    }

    [Theory]
    [InlineData("85.10.20.30", "85.10.20.30", NatVerdict.Direct)]
    [InlineData("85.10.20.30", "91.1.1.1", NatVerdict.Blocked)]
    [InlineData("100.70.1.2", null, NatVerdict.Blocked)]
    [InlineData("192.168.1.2", "85.10.20.30", NatVerdict.Blocked)]
    [InlineData(null, "85.10.20.30", NatVerdict.Unknown)]
    [InlineData("85.10.20.30", null, NatVerdict.Unknown)]
    [InlineData("0.0.0.0", "85.10.20.30", NatVerdict.Unknown)]
    public void EvaluatesNat(string? router, string? publicIp, NatVerdict expected) =>
        Assert.Equal(expected, PortForwarding.Evaluate(
            router == null ? null : IPAddress.Parse(router),
            publicIp == null ? null : IPAddress.Parse(publicIp)));

    [Theory]
    [InlineData("Tailscale", "Tailscale Tunnel", VirtualNetwork.Tailscale)]
    [InlineData("tailscale0", "", VirtualNetwork.Tailscale)]
    [InlineData("ZeroTier One [8056c2e21c000001]", "ZeroTier Virtual Port", VirtualNetwork.ZeroTier)]
    [InlineData("ztrf26xyab", "ztrf26xyab", VirtualNetwork.ZeroTier)]
    [InlineData("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", VirtualNetwork.Radmin)]
    [InlineData("Hamachi", "LogMeIn Hamachi Virtual Ethernet Adapter", VirtualNetwork.Hamachi)]
    [InlineData("Ethernet 2", "Realtek PCIe GbE Family Controller", VirtualNetwork.None)]
    public void ClassifiesVirtualNetworks(string name, string description, VirtualNetwork expected) =>
        Assert.Equal(expected, LocalNetworks.Classify(name, description));
}
