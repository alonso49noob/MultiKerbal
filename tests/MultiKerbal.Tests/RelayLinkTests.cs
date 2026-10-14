using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;
using MultiKerbal.Server;
using MultiKerbal.Server.Relay;

namespace MultiKerbal.Tests;

public sealed class RelayLinkTests
{
    // El relay de pruebas caduca a 1 s; estos tests necesitan que nadie caduque salvo cuando se prueba justo eso.
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private static RelayLink LinkOf(TestServer server) => server.Host.RelayLink ?? throw new InvalidOperationException("El servidor no usa el relay");

    private static bool WaitRegistered(TestServer server, double seconds = 3) =>
        TestRelay.WaitFor(() => LinkOf(server).State == RelayLinkState.Registered, seconds);

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Server_RegistersWithTheRelay_AndKeepsItsCodeAfterARestart()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);

        Assert.True(WaitRegistered(server));
        string code = LinkOf(server).Code;
        Assert.True(ServerCode.TryNormalize(code, out _));
        Assert.True(relay.Host.TryGetServer(code, out ServerSession? session));
        Assert.Equal("Servidor de pruebas", session.Info.Name);
        Assert.Equal(ProtocolInfo.Version, session.Info.GameProtocol);
        Assert.Equal(server.Port, session.GamePort);

        server.Restart();

        Assert.True(WaitRegistered(server));
        Assert.Equal(code, LinkOf(server).Code);
        Assert.Equal(1, relay.Host.LiveServerCount);
    }

    [Fact]
    public void Server_WithoutRelayAvailable_StartsAnyway_AndRegistersWhenTheRelayAppears()
    {
        int port = ReserveFreePort();
        using var server = new TestServer(relayAddress: $"127.0.0.1:{port}");

        // El relay no existe: el servidor funciona, se entra por IP y el estado es Connecting.
        Assert.Equal(RelayLinkState.Connecting, LinkOf(server).State);
        using TestClient client = server.Join("Jeb");
        Assert.True(client.Welcome!.Accepted);
        Assert.Equal(RelayLinkState.Connecting, LinkOf(server).State);

        using var relay = new TestRelay(o =>
        {
            o.Port = port;
            o.HeartbeatTimeout = TimeSpan.FromSeconds(30);
        });

        Assert.True(WaitRegistered(server, seconds: 2));
        Assert.True(relay.Host.TryGetServer(LinkOf(server).Code, out _));
    }

    [Fact]
    public void Server_ComesBackAfterTheRelayRestarts_WithTheSameCode()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        Assert.True(WaitRegistered(server));
        string code = LinkOf(server).Code;

        relay.Restart();

        // El relay nuevo tiene el registro vacío: solo vale cuando el servidor se ha vuelto a registrar en él.
        Assert.True(TestRelay.WaitFor(() => relay.Host.TryGetServer(code, out _), seconds: 3));
        Assert.True(WaitRegistered(server));
        Assert.Equal(code, LinkOf(server).Code);
    }

    [Fact]
    public void Heartbeat_CarriesThePlayerCount()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        Assert.True(WaitRegistered(server));
        string code = LinkOf(server).Code;
        Assert.True(relay.Host.TryGetServer(code, out ServerSession? session));
        Assert.Equal(0, session.Info.Players);

        using TestClient client = server.Join("Jeb");

        Assert.True(TestRelay.WaitFor(() => relay.Host.TryGetServer(code, out ServerSession? s) && s.Info.Players == 1, seconds: 2));

        client.Dispose();
        Assert.True(TestRelay.WaitFor(() => relay.Host.TryGetServer(code, out ServerSession? s) && s.Info.Players == 0, seconds: 3));
    }

    [Fact]
    public void Server_KeepsAliveBeyondTheRelayExpiry()
    {
        using var relay = new TestRelay(); // caducidad de 1 s: sin latidos del servidor, se caería
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        Assert.True(WaitRegistered(server));
        string code = LinkOf(server).Code;

        Thread.Sleep(2300);

        Assert.True(relay.Host.TryGetServer(code, out _));
        Assert.Equal(RelayLinkState.Registered, LinkOf(server).State);
    }

    [Fact]
    public void BannedServer_ShowsRejected_WithTheReason_AndDoesNotRetry()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        Assert.True(WaitRegistered(server));
        string code = LinkOf(server).Code;

        relay.Host.ExecuteCommand($"ban {code}");

        Assert.True(TestRelay.WaitFor(() => LinkOf(server).State == RelayLinkState.Rejected, seconds: 3));
        Assert.False(string.IsNullOrEmpty(LinkOf(server).Error));
        Assert.Equal(string.Empty, LinkOf(server).Code);

        // Tras el desbaneo no vuelve solo: Rejected es definitivo hasta reiniciar.
        relay.Host.ExecuteCommand($"unban {code}");
        Thread.Sleep(800);
        Assert.Equal(RelayLinkState.Rejected, LinkOf(server).State);
        Assert.Equal(0, relay.Host.LiveServerCount);
    }

    [Fact]
    public void StateChanges_AreReportedThroughTheChangedCallback()
    {
        using var relay = new TestRelay(LongLived);
        string dir = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "link-" + Guid.NewGuid().ToString("N"));
        try
        {
            var states = new List<RelayLinkState>();
            RelayLink? link = null;
            link = new RelayLink(
                relay.RelayAddress,
                RelayKey.LoadOrCreate(dir),
                gamePort: 6750,
                () => new RelayServerInfo { Name = "x", MaxPlayers = 8, GameProtocol = ProtocolInfo.Version, ModVersion = "0" },
                network: null!,
                changed: () =>
                {
                    lock (states)
                        states.Add(link!.State);
                },
                new RelayLinkOptions { Heartbeat = TimeSpan.FromMilliseconds(250), RetryInitial = TimeSpan.FromMilliseconds(200), RetryMax = TimeSpan.FromSeconds(1) });

            link.Start();
            try
            {
                Assert.True(TestRelay.WaitFor(() => link.State == RelayLinkState.Registered));
            }
            finally
            {
                link.Stop();
            }

            lock (states)
            {
                Assert.Contains(RelayLinkState.Connecting, states);
                Assert.Contains(RelayLinkState.Registered, states);
            }

            // Stop cierra el control: el relay deja de contar el servidor enseguida, sin esperar a la caducidad.
            Assert.True(TestRelay.WaitFor(() => relay.Host.LiveServerCount == 0, seconds: 1));
            Assert.Equal(RelayLinkState.Off, link.State);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Stop_WhileTheRelayIsDown_ReturnsPromptly()
    {
        int port = ReserveFreePort();
        var server = new TestServer(relayAddress: $"127.0.0.1:{port}");
        Assert.Equal(RelayLinkState.Connecting, LinkOf(server).State);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        server.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Tardó {watch.Elapsed}");
    }

    [Fact]
    public void Server_WithoutRelayAddress_DoesNotUseTheRelay()
    {
        using var server = new TestServer();

        Assert.Null(server.Host.RelayLink);
        Assert.False(server.Host.Config.Relay);
    }

    [Fact]
    public void RelayKey_IsCreatedOnce_AndReused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "key-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] first = RelayKey.LoadOrCreate(dir);
            byte[] second = RelayKey.LoadOrCreate(dir);

            Assert.Equal(RelayRegisterMessage.KeyLength, first.Length);
            Assert.Equal(first, second);
            Assert.Equal(first, File.ReadAllBytes(Path.Combine(dir, "relay.key")));
            Assert.NotEqual(first, RelayKey.LoadOrCreate(dir + "-otra"));
        }
        finally
        {
            foreach (string d in new[] { dir, dir + "-otra" })
            {
                try
                {
                    Directory.Delete(d, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void RelayKey_WithWrongSize_IsNotOverwritten()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "key-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "relay.key");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

            Assert.Throws<InvalidDataException>(() => RelayKey.LoadOrCreate(dir));

            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:abc")]
    [InlineData("host:0")]
    [InlineData("host con espacios")]
    public void Config_WithInvalidRelayAddress_DisablesTheRelay(string address)
    {
        Log.ConsoleEnabled = false;
        var config = new ServerConfig { RelayAddress = address };

        config.Validate();

        Assert.False(config.Relay);
    }

    [Theory]
    [InlineData("multikerbal.duckdns.org")]
    [InlineData("10.0.0.5:6761")]
    [InlineData("[::1]:6760")]
    public void Config_WithValidRelayAddress_KeepsTheRelay(string address)
    {
        var config = new ServerConfig { RelayAddress = address };

        config.Validate();

        Assert.True(config.Relay);
    }

    [Fact]
    public void Config_Defaults_UseTheOfficialRelay_AndAPrivateServer()
    {
        var config = new ServerConfig();

        Assert.True(config.Relay);
        Assert.Equal(ProtocolInfo.DefaultRelayAddress, config.RelayAddress);
        Assert.False(config.Public);
    }
}
