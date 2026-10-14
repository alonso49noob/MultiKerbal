using System.Diagnostics;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;
using MultiKerbal.Server.Net;

namespace MultiKerbal.Tests;

public sealed class RelayJoinTests
{
    // El relay de pruebas caduca a 1 s; aquí nadie debe caducar.
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Servidor real que no se puede alcanzar directamente (anuncia un puerto cerrado): quien entra con el código
    /// pasa siempre por el relay, que es lo que prueban estos tests.
    /// </summary>
    private static TestServer RelayOnlyServer(TestRelay relay) => new(relayAddress: relay.RelayAddress, reachableDirectly: false);

    /// <summary>Un servidor "en bruto" registrado en el relay; devuelve su código.</summary>
    private static string RegisterRawServer(RawRelayClient control, byte seed, int gameProtocol = ProtocolInfo.Version)
    {
        RelayRegisterMessage register = RawRelayClient.Register(RawRelayClient.Key(seed));
        register.Info.GameProtocol = gameProtocol;
        return control.Request<RelayRegisteredMessage>(register).Code;
    }

    [Fact]
    public void Player_JoinsWithACode_ThroughTheRelay_AndChatFlowsBothWays()
    {
        using var relay = new TestRelay(LongLived);
        using var server = RelayOnlyServer(relay);
        string code = server.WaitForRelayCode();

        TestClient jeb = server.Track(TestClient.JoinWithCode(relay.RelayAddress, ServerCode.Format(code).ToLowerInvariant(), "Jeb"));

        Assert.True(jeb.Welcome!.Accepted);
        Assert.True(jeb.Net.ViaRelay);
        Assert.Equal("Servidor de pruebas", jeb.Net.ServerName);

        TestClient bill = server.Join("Bill");
        jeb.WaitFor<PlayerJoinedMessage>(m => m.Player.Name == "Bill");

        jeb.Send(new ChatMessage { Text = "hola desde el relay" });
        bill.WaitFor<ChatMessage>(m => m.Text == "hola desde el relay" && m.SenderId == jeb.Welcome.PlayerId);

        bill.Send(new ChatMessage { Text = "recibido" });
        jeb.WaitFor<ChatMessage>(m => m.Text == "recibido" && m.SenderId == bill.Welcome!.PlayerId);
    }

    [Fact]
    public void AdoptedConnection_ShowsThePlayerAddress_ButIsNotLoopback()
    {
        using var relay = new TestRelay(LongLived);
        using var server = RelayOnlyServer(relay);
        string code = server.WaitForRelayCode();

        server.Track(TestClient.JoinWithCode(relay.RelayAddress, code, "Jeb"));
        server.Join("Bill");

        ClientConnection[] connections = server.Host.Connections;
        ClientConnection viaRelay = Assert.Single(connections, c => c.RelaySessionId != 0);
        Assert.Contains("127.0.0.1", viaRelay.RemoteEndPoint);
        Assert.False(viaRelay.IsLoopback);

        // La conexión directa sí sale del socket de loopback.
        ClientConnection direct = Assert.Single(connections, c => c.RelaySessionId == 0);
        Assert.True(direct.IsLoopback);
    }

    [Fact]
    public void Kick_DisconnectsThePlayerThroughTheRelay_AndFreesTheSession()
    {
        using var relay = new TestRelay(LongLived);
        using var server = RelayOnlyServer(relay);
        string code = server.WaitForRelayCode();
        TestClient jeb = server.Track(TestClient.JoinWithCode(relay.RelayAddress, code, "Jeb"));
        Assert.Equal(1, relay.Host.RelaySessionCount);

        server.Host.EnqueueCommand("kick Jeb por pruebas");

        Assert.Contains("Expulsado", jeb.WaitForEvent(NetEventType.Disconnected).Reason);
        Assert.True(TestRelay.WaitFor(() => relay.Host.RelaySessionCount == 0, seconds: 3));
    }

    [Fact]
    public void UnknownCode_SaysThereIsNoServer()
    {
        using var relay = new TestRelay(LongLived);
        using TestClient client = TestClient.StartWithCode(relay.RelayAddress, "K7M-Q2P");

        Assert.Contains("No hay ningún servidor", client.WaitForEvent(NetEventType.Disconnected).Reason);
        Assert.False(client.Net.ViaRelay);
    }

    [Fact]
    public void RelayDown_SaysTheRelayCannotBeReached()
    {
        int port = TestServer.ReserveFreePort();
        using TestClient client = TestClient.StartWithCode($"127.0.0.1:{port}", "K7M-Q2P");

        Assert.Contains("No se puede contactar con el relay", client.WaitForEvent(NetEventType.Disconnected).Reason);
    }

    [Fact]
    public void ServerWithAnotherProtocol_IsRefusedBeforeJoining()
    {
        using var relay = new TestRelay(LongLived);
        RawRelayClient control = relay.Connect();
        string code = RegisterRawServer(control, seed: 3, gameProtocol: ProtocolInfo.Version + 1);

        using TestClient client = TestClient.StartWithCode(relay.RelayAddress, code);

        Assert.Contains("otra versión de MultiKerbal", client.WaitForEvent(NetEventType.Disconnected).Reason);
        Assert.Equal(0, relay.Host.RelaySessionCount);
    }

    [Fact]
    public void FullServer_RefusesTheNextPlayer_UntilASessionCloses()
    {
        using var relay = new TestRelay(o =>
        {
            o.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            o.MaxSessionsPerServer = 1;
        });
        using var server = RelayOnlyServer(relay);
        string code = server.WaitForRelayCode();
        TestClient jeb = server.Track(TestClient.JoinWithCode(relay.RelayAddress, code, "Jeb"));

        using (TestClient bill = TestClient.StartWithCode(relay.RelayAddress, code))
            Assert.Contains("El servidor está lleno", bill.WaitForEvent(NetEventType.Disconnected).Reason);

        // Al salir el primero se libera su sitio.
        jeb.Dispose();
        Assert.True(TestRelay.WaitFor(() => relay.Host.RelaySessionCount == 0, seconds: 3));
        TestClient val = server.Track(TestClient.JoinWithCode(relay.RelayAddress, code, "Val"));
        Assert.True(val.Net.ViaRelay);
    }

    [Fact]
    public void ServerThatNeverAttaches_GivesTheNoResponseError()
    {
        using var relay = new TestRelay(o =>
        {
            o.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            o.AttachTimeout = TimeSpan.FromSeconds(1);
        });
        RawRelayClient control = relay.Connect();
        string code = RegisterRawServer(control, seed: 5);

        using TestClient client = TestClient.StartWithCode(relay.RelayAddress, code);

        // El relay avisa al servidor (que no hace nada) y, pasado el plazo de enganche, contesta al jugador.
        var incoming = control.ReceiveControl<RelayIncomingMessage>();
        Assert.Contains("127.0.0.1", incoming.ClientAddress);
        Assert.Contains("El servidor no responde a través del relay", client.WaitForEvent(NetEventType.Disconnected).Reason);
        Assert.True(TestRelay.WaitFor(() => relay.Host.RelaySessionCount == 0, seconds: 1));
    }

    [Fact]
    public void AttachWithTheWrongSecret_IsRefused()
    {
        using var relay = new TestRelay(o =>
        {
            o.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            o.AttachTimeout = TimeSpan.FromSeconds(1);
        });
        RawRelayClient control = relay.Connect();
        string code = RegisterRawServer(control, seed: 6);
        RawRelayClient player = relay.Connect();
        player.Send(new RelayJoinMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = code });
        var incoming = control.ReceiveControl<RelayIncomingMessage>();

        RawRelayClient intruder = relay.Connect();
        intruder.Send(new RelayAttachMessage { RelayVersion = ProtocolInfo.RelayVersion, SessionId = incoming.SessionId, Secret = incoming.Secret + 1 });

        Assert.True(intruder.WaitForClose(seconds: 2));
        var result = Assert.IsType<RelayJoinResultMessage>(player.Receive());
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Relay_ThrottlesTheSessionInBothDirections()
    {
        const int rate = 20_000;
        using var relay = new TestRelay(o =>
        {
            o.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            o.BytesPerSecond = rate;
        });
        RawRelayClient control = relay.Connect();
        string code = RegisterRawServer(control, seed: 9);

        RawRelayClient player = relay.Connect();
        player.Send(new RelayJoinMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = code });
        var incoming = control.ReceiveControl<RelayIncomingMessage>();
        RawRelayClient attach = relay.Connect();
        attach.Send(new RelayAttachMessage { RelayVersion = ProtocolInfo.RelayVersion, SessionId = incoming.SessionId, Secret = incoming.Secret });
        Assert.True(Assert.IsType<RelayJoinResultMessage>(player.Receive()).Ok);

        // El doble del ritmo: el primer segundo pasa de golpe (cubo lleno) y el resto tarda otro segundo.
        byte[] up = Enumerable.Range(0, 2 * rate).Select(i => (byte)i).ToArray();
        byte[] down = Enumerable.Range(0, 2 * rate).Select(i => (byte)(255 - i)).ToArray();
        var watch = Stopwatch.StartNew();
        Task<double> upTime = Task.Run(() =>
        {
            Assert.Equal(up, attach.ReceiveRaw(up.Length, seconds: 4));
            return watch.Elapsed.TotalSeconds;
        });
        Task<double> downTime = Task.Run(() =>
        {
            Assert.Equal(down, player.ReceiveRaw(down.Length, seconds: 4));
            return watch.Elapsed.TotalSeconds;
        });
        player.SendRaw(up);
        attach.SendRaw(down);

        Assert.InRange(await upTime, 0.8, 3);
        Assert.InRange(await downTime, 0.8, 3);

        // Al cerrar un lado, el relay le pasa el cierre al otro y, como mucho tras su margen (corto en las pruebas), olvida la sesión.
        player.Dispose();
        Assert.True(attach.WaitForClose(seconds: 1));
        Assert.True(TestRelay.WaitFor(() => relay.Host.RelaySessionCount == 0, seconds: 3));
    }
}
