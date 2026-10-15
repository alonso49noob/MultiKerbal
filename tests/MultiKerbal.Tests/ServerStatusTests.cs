using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;
using MultiKerbal.Server;

namespace MultiKerbal.Tests;

/// <summary>Consulta de estado y control local en el puerto de juego, y código del servidor dentro de la partida.</summary>
public sealed class ServerStatusTests
{
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private static ServerStatusResponseMessage Query(TestServer server, out bool portOpen) =>
        ServerStatusQuery.Query("127.0.0.1", server.Port, 3000, out portOpen)!;

    /// <summary>Espera a que el servidor haya procesado lo que se le mandó antes, con un chat de ida y vuelta.</summary>
    private static void RoundTrip(TestClient client, string text)
    {
        client.Send(new ChatMessage { Text = text });
        client.WaitFor<ChatMessage>(m => m.Text == text);
    }

    [Fact]
    public void ProtocolVersion_IsSix_AndAnOlderClientIsRejected()
    {
        Assert.Equal(6, ProtocolInfo.Version);

        using var server = new TestServer();
        TestClient old = server.Track(TestClient.Connect(server.Port));
        old.Send(new HandshakeRequestMessage { ProtocolVersion = 5, PlayerName = "Jeb", AccountPassword = TestClient.DefaultAccountPassword });

        HandshakeResponseMessage response = old.WaitFor<HandshakeResponseMessage>();
        Assert.False(response.Accepted);
        Assert.Contains("protocolo", response.RejectReason);
    }

    [Fact]
    public void Query_ToAServerWithRelay_GivesItsStatus()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, serverName: "Estación Kerbin");
        string code = server.WaitForRelayCode();

        ServerStatusResponseMessage status = Query(server, out bool portOpen);

        Assert.True(portOpen);
        Assert.NotNull(status);
        Assert.Equal(ProtocolInfo.Version, status.GameProtocol);
        Assert.Equal("Estación Kerbin", status.Name);
        Assert.Equal(0, status.Players);
        Assert.Equal(server.Host.Config.MaxPlayers, status.MaxPlayers);
        Assert.False(status.HasPassword);
        Assert.Equal(RelayLinkState.Registered, status.RelayState);
        Assert.Equal(code, status.Code); // sin guion en el cable: el mod lo formatea
        Assert.False(status.IsPublic);

        server.Join("Jeb");
        Assert.Equal(1, Query(server, out _).Players);
    }

    [Fact]
    public void Query_ToAServerWithoutRelay_ReportsRelayOff()
    {
        using var server = new TestServer();

        ServerStatusResponseMessage status = Query(server, out bool portOpen);

        Assert.True(portOpen);
        Assert.Equal(RelayLinkState.Off, status.RelayState);
        Assert.Equal(string.Empty, status.Code);
        Assert.False(status.Direct);
    }

    [Fact]
    public void Query_DoesNotCreateAPlayer_NorKeepTheConnectionOpen()
    {
        using var server = new TestServer();

        Query(server, out _);

        Assert.True(TestRelay.WaitFor(() => server.Host.Connections.Length == 0, seconds: 3), "La conexión de la consulta quedó abierta");
        server.Join("Jeb"); // y el servidor sigue aceptando jugadores con normalidad
    }

    [Fact]
    public void Query_ToAClosedPort_GivesNull_AndPortIsNotOpen()
    {
        ServerStatusResponseMessage? status = ServerStatusQuery.Query("127.0.0.1", TestServer.ReserveFreePort(), 2000, out bool portOpen);

        Assert.Null(status);
        Assert.False(portOpen);
    }

    [Fact]
    public void Query_ToAListenerThatClosesWithoutAnswering_GivesNull_ButPortIsOpen()
    {
        // Es lo que hace un servidor de una versión anterior al recibir un mensaje que no conoce.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepter = new Thread(() =>
            {
                using TcpClient client = listener.AcceptTcpClient();
            });
            accepter.Start();

            ServerStatusResponseMessage? status = ServerStatusQuery.Query("127.0.0.1", port, 2000, out bool portOpen);

            Assert.Null(status);
            Assert.True(portOpen);
            Assert.True(accepter.Join(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void SendControl_SetPublic_FromLoopback_SwitchesPublicOnAndOff()
    {
        using var server = new TestServer();
        Assert.False(server.Host.Config.Public);

        Assert.True(ServerStatusQuery.SendControl("127.0.0.1", server.Port, ServerControlAction.SetPublic, 3000));
        Assert.True(TestRelay.WaitFor(() => server.Host.Config.Public));
        Assert.True(Query(server, out _).IsPublic);

        Assert.True(ServerStatusQuery.SendControl("127.0.0.1", server.Port, ServerControlAction.SetPrivate, 3000));
        Assert.True(TestRelay.WaitFor(() => !server.Host.Config.Public));
        Assert.False(Query(server, out _).IsPublic);
    }

    [Fact]
    public void SendControl_SetPublic_IsSavedLikeThePublicCommand()
    {
        string path = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "server-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var server = new TestServer();
            server.Host.ConfigPath = path;

            Assert.True(ServerStatusQuery.SendControl("127.0.0.1", server.Port, ServerControlAction.SetPublic, 3000));

            Assert.True(TestRelay.WaitFor(() => File.Exists(path) && ServerConfig.LoadOrCreate(path).Public), "No se guardó server.json");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SendControl_Recheck_StartsANewProbe()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        server.WaitForRelayCode();
        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount >= 1, seconds: 3));
        int before = relay.Host.ProbeCount;

        Assert.True(ServerStatusQuery.SendControl("127.0.0.1", server.Port, ServerControlAction.Recheck, 3000));

        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount > before, seconds: 3));
    }

    [Fact]
    public void SendControl_UnknownAction_GivesFalse_AndChangesNothing()
    {
        using var server = new TestServer();

        Assert.False(ServerStatusQuery.SendControl("127.0.0.1", server.Port, (ServerControlAction)99, 3000));

        Assert.False(server.Host.Config.Public);
        Assert.True(TestRelay.WaitFor(() => server.Host.Connections.Length == 0, seconds: 3));
    }

    [Fact]
    public void SendControl_ToAClosedPort_GivesFalse()
    {
        Assert.False(ServerStatusQuery.SendControl("127.0.0.1", TestServer.ReserveFreePort(), ServerControlAction.SetPublic, 2000));
    }

    [Fact]
    public void ServerControl_OverARelayConnection_IsIgnoredAndClosed()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        string code = server.WaitForRelayCode();

        TestClient client = server.Track(TestClient.StartWithCode(relay.RelayAddress, code));
        TestClient.WaitUntil(() => client.Net.State != NetClientState.Connecting);
        client.WaitForEvent(NetEventType.Connected);
        Assert.True(client.Net.ViaRelay, "La conexión no pasó por el relay: la prueba no sirve");

        client.Send(new ServerControlMessage { Action = ServerControlAction.SetPublic });

        client.WaitForEvent(NetEventType.Disconnected);
        Assert.False(server.Host.Config.Public);
        Assert.True(TestRelay.WaitFor(() => server.Host.Connections.Length == 0, seconds: 3));
    }

    [Fact]
    public void StatusRequest_OverARelayConnection_IsAnswered()
    {
        // La consulta de estado la puede hacer cualquiera, también por el relay (con la IP ya se puede entrar).
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        string code = server.WaitForRelayCode();

        TestClient client = server.Track(TestClient.StartWithCode(relay.RelayAddress, code));
        TestClient.WaitUntil(() => client.Net.State != NetClientState.Connecting);
        client.WaitForEvent(NetEventType.Connected);
        Assert.True(client.Net.ViaRelay);

        client.Send(new ServerStatusRequestMessage());

        ServerStatusResponseMessage status = client.WaitFor<ServerStatusResponseMessage>();
        Assert.Equal(code, status.Code);
    }

    [Fact]
    public void StatusAndControl_AfterTheHandshake_AreIgnored()
    {
        using var server = new TestServer();
        TestClient jeb = server.Join("Jeb");

        jeb.Send(new ServerControlMessage { Action = ServerControlAction.SetPublic });
        jeb.Send(new ServerStatusRequestMessage());
        RoundTrip(jeb, "sigo aquí");

        Assert.False(server.Host.Config.Public);
        Assert.Empty(jeb.Received<ServerStatusResponseMessage>());
    }

    [Fact]
    public void Player_ReceivesTheServerCode_WhenJoining()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        string code = server.WaitForRelayCode();
        Assert.True(TestRelay.WaitFor(() => server.Host.RelayLink!.Direct, seconds: 3));

        TestClient jeb = server.Join("Jeb");

        ServerCodeMessage message = jeb.WaitFor<ServerCodeMessage>();
        Assert.Equal(code, message.Code);
        Assert.True(message.Direct);
    }

    [Fact]
    public void Player_ReceivesAnotherServerCode_WhenTheServerRegistersAgain()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        string code = server.WaitForRelayCode();
        Assert.True(TestRelay.WaitFor(() => server.Host.RelayLink!.Direct, seconds: 3));
        TestClient jeb = server.Join("Jeb");
        jeb.WaitFor<ServerCodeMessage>(m => m.Direct);

        relay.Restart();

        // Al volver a registrarse hay código otra vez (el mismo): se avisa de nuevo.
        ServerCodeMessage again = jeb.WaitFor<ServerCodeMessage>(m => m.Code == code);
        Assert.Equal(code, again.Code);
    }

    [Fact]
    public void ServerCode_IsNotResent_WhenNothingChanged()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        server.WaitForRelayCode();
        Assert.True(TestRelay.WaitFor(() => server.Host.RelayLink!.Direct, seconds: 3));
        TestClient jeb = server.Join("Jeb");
        jeb.WaitFor<ServerCodeMessage>(m => m.Direct);

        // Recomprobaciones (que repiten el mismo resultado) y más latidos: ningún cambio de código ni de acceso.
        for (int i = 0; i < 3; i++)
            server.Host.RelayLink!.Recheck();
        server.Host.RelayLink!.HeartbeatNow();
        Thread.Sleep(600);
        RoundTrip(jeb, "nada nuevo");

        Assert.Empty(jeb.Received<ServerCodeMessage>());
    }

    [Fact]
    public void Player_ReceivesNoServerCode_WithoutRelay()
    {
        using var server = new TestServer();
        TestClient jeb = server.Join("Jeb");

        RoundTrip(jeb, "hola");

        Assert.Empty(jeb.Received<ServerCodeMessage>());
    }
}
