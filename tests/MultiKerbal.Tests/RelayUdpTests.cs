using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Vessels;
using MultiKerbal.Relay;

namespace MultiKerbal.Tests;

public sealed class RelayUdpTests
{
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private static readonly Guid VesselId = Guid.Parse("0b7d2c1e-8f4a-4d2b-a6c3-5e9f1d7a3b22");
    private static readonly Guid OtherVesselId = Guid.Parse("7c1e0b2d-4a8f-4b2d-9c3a-1d7a3b225e9f");

    /// <summary>Un jugador que entra por el relay y abre su UDP (que también pasa por el relay).</summary>
    private static TestClient JoinThroughRelayWithUdp(TestRelay relay, TestServer server, string name)
    {
        TestClient client = server.Track(TestClient.JoinWithCode(relay.RelayAddress, server.WaitForRelayCode(), name));
        Assert.True(client.Net.ViaRelay);
        client.Net.StartUdp(client.Welcome!.UdpToken);
        Assert.True(TestRelay.WaitFor(() => client.Net.UdpReady, seconds: 3), "El UDP por el relay no llegó a estar listo");
        return client;
    }

    private static VesselProtoMessage Proto(Guid id) =>
        new() { VesselId = id, VesselName = "Kerbal X", Access = VesselAccess.Shared, Data = [1, 2, 3] };

    /// <summary>La actualización de ejemplo de <see cref="SampleMessages"/>, para la nave indicada.</summary>
    private static VesselUpdateMessage Update(Guid id, double universalTime)
    {
        VesselUpdateMessage update = SampleMessages.All().OfType<VesselUpdateMessage>().First();
        update.VesselId = id;
        update.UniversalTime = universalTime;
        update.Epoch = universalTime;
        return update;
    }

    /// <summary>
    /// Manda la actualización sin garantía (UDP) cada poco hasta que el otro la recibe: un datagrama suelto se
    /// puede perder, pero si el camino no funciona no llega ninguno.
    /// </summary>
    private static void SendUnreliableUntilReceived(TestClient from, TestClient to, VesselUpdateMessage update)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            from.Send(update, Delivery.Unreliable);
            if (TestRelay.WaitFor(() => to.Received<VesselUpdateMessage>().Any(m => m.VesselId == update.VesselId && m.UniversalTime == update.UniversalTime), seconds: 0.2))
                return;
        }

        Assert.Fail($"La actualización de {update.VesselId} (UT {update.UniversalTime}) no llegó por UDP");
    }

    [Fact]
    public void PlayerThroughTheRelay_GetsUdpReady_InUnderThreeSeconds()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        TestClient jeb = server.Track(TestClient.JoinWithCode(relay.RelayAddress, server.WaitForRelayCode(), "Jeb"));
        Assert.True(jeb.Net.ViaRelay);

        var watch = Stopwatch.StartNew();
        jeb.Net.StartUdp(jeb.Welcome!.UdpToken);

        Assert.True(TestRelay.WaitFor(() => jeb.Net.UdpReady, seconds: 3));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Tardó {watch.Elapsed.TotalSeconds:0.00} s");
    }

    [Fact]
    public void UnreliableVesselUpdate_FromAPlayerThroughTheRelay_ReachesADirectPlayer()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        TestClient jeb = JoinThroughRelayWithUdp(relay, server, "Jeb");
        TestClient bill = server.Join("Bill");

        jeb.Send(Proto(VesselId));
        bill.WaitFor<VesselProtoMessage>(m => m.VesselId == VesselId);

        SendUnreliableUntilReceived(jeb, bill, Update(VesselId, 10));
    }

    [Fact]
    public void PlayerUdpAddressChanges_MidGame_AndPacketsKeepFlowingBothWays()
    {
        // Punto de revisión 5: el router del jugador le cambia el puerto UDP. El relay sigue la última dirección
        // que manda una ficha válida.
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        TestClient jeb = JoinThroughRelayWithUdp(relay, server, "Jeb");
        TestClient bill = server.Join("Bill");
        jeb.Send(Proto(VesselId));
        bill.WaitFor<VesselProtoMessage>(m => m.VesselId == VesselId);
        bill.Send(Proto(OtherVesselId));
        jeb.WaitFor<VesselProtoMessage>(m => m.VesselId == OtherVesselId);
        SendUnreliableUntilReceived(jeb, bill, Update(VesselId, 10));
        SendUnreliableUntilReceived(bill, jeb, Update(OtherVesselId, 10));

        jeb.Net.RebindUdpForTests();

        // Jeb → servidor desde el puerto nuevo; con eso el relay aprende la dirección nueva...
        SendUnreliableUntilReceived(jeb, bill, Update(VesselId, 20));

        // ...y lo que el servidor manda a Jeb por UDP le llega al puerto nuevo (el viejo ya está cerrado).
        SendUnreliableUntilReceived(bill, jeb, Update(OtherVesselId, 20));
        Assert.True(jeb.Net.UdpReady);
    }

    [Fact]
    public void UdpToken_OnlyCountsForTheServersOwnSessions_AndIsForgottenWhenTheSessionCloses()
    {
        using var relay = new TestRelay(LongLived);
        var relayUdp = new IPEndPoint(IPAddress.Loopback, relay.Port);

        // Dos servidores "en bruto"; el B tiene su socket de juego y saluda al relay desde él.
        RawRelayClient controlA = relay.Connect();
        controlA.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(1)));
        RawRelayClient controlB = relay.Connect();
        var registeredB = controlB.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(2)));
        using UdpClient serverUdp = NewUdp();
        serverUdp.Send(FrameCodec.EncodeUdp(new RelayUdpHelloMessage { UdpSecret = registeredB.UdpSecret }, 0), relayUdp);

        // Un jugador entra en B por el relay y B se engancha.
        RawRelayClient player = relay.Connect();
        player.Send(new RelayJoinMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = registeredB.Code });
        var incoming = controlB.ReceiveControl<RelayIncomingMessage>();
        RawRelayClient attach = relay.Connect();
        attach.Send(new RelayAttachMessage { RelayVersion = ProtocolInfo.RelayVersion, SessionId = incoming.SessionId, Secret = incoming.Secret });
        Assert.True(Assert.IsType<RelayJoinResultMessage>(player.Receive()).Ok);

        // B da la ficha de su sesión: el UDP del jugador llega a B, y la respuesta de B vuelve al jugador.
        const ulong token = 0x1122334455667788;
        controlB.Send(new RelayUdpTokenMessage { SessionId = incoming.SessionId, Token = token });
        using UdpClient playerUdp = NewUdp();
        byte[] up = FrameCodec.EncodeUdp(new UdpBindMessage(), token);
        Assert.True(SendUntilReceived(playerUdp, up, relayUdp, serverUdp, up), "El UDP del jugador no llegó al servidor");
        byte[] down = FrameCodec.EncodeUdp(new UdpBindAckMessage(), token);
        Assert.True(SendUntilReceived(serverUdp, down, relayUdp, playerUdp, down), "La respuesta del servidor no llegó al jugador");

        // A intenta dar una ficha para una sesión que no es suya: el relay no le hace caso.
        Drain(serverUdp);
        const ulong stolen = 0x0102030405060708;
        controlA.Send(new RelayUdpTokenMessage { SessionId = incoming.SessionId, Token = stolen });
        Assert.False(SendUntilReceived(playerUdp, FrameCodec.EncodeUdp(new UdpBindMessage(), stolen), relayUdp, serverUdp, null, seconds: 0.5));

        // Al cerrarse la sesión, el relay olvida la ficha.
        player.Dispose();
        Assert.True(TestRelay.WaitFor(() => relay.Host.RelaySessionCount == 0, seconds: 3));
        Drain(serverUdp);
        Assert.False(SendUntilReceived(playerUdp, up, relayUdp, serverUdp, null, seconds: 0.5));
    }

    [Fact]
    public void ServerControlDropsAndComesBack_WhileAPlayerThroughTheRelayKeepsItsUdp()
    {
        // Las sesiones de los jugadores siguen vivas aunque se caiga el control del servidor. Los datagramas que el
        // servidor manda mientras tanto no pueden tomarse por los del jugador (si no, al volver, el relay le
        // mandaría al propio servidor todo lo que es para el jugador).
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        TestClient jeb = JoinThroughRelayWithUdp(relay, server, "Jeb");
        TestClient bill = server.Join("Bill");
        jeb.Send(Proto(VesselId));
        bill.WaitFor<VesselProtoMessage>(m => m.VesselId == VesselId);
        bill.Send(Proto(OtherVesselId));
        jeb.WaitFor<VesselProtoMessage>(m => m.VesselId == OtherVesselId);
        SendUnreliableUntilReceived(bill, jeb, Update(OtherVesselId, 10));
        string code = server.WaitForRelayCode();
        Assert.True(relay.Host.TryGetServer(code, out ServerSession? before));

        // El relay corta el control; el servidor se vuelve a registrar solo. Mientras tanto Bill sigue pilotando,
        // así que el servidor sigue mandando a Jeb por UDP (a través del relay).
        before.Close();
        double ut = 11;
        Assert.True(TestRelay.WaitFor(() =>
        {
            bill.Send(Update(OtherVesselId, ut++), Delivery.Unreliable);
            return relay.Host.TryGetServer(code, out ServerSession? again) && again != before && again.UdpEndPoint != null;
        }, seconds: 4), "El servidor no volvió a registrarse");
        Assert.Equal(1, relay.Host.RelaySessionCount);

        // Sin que Jeb mande nada por UDP, lo que el servidor le manda le sigue llegando, y al revés también.
        SendUnreliableUntilReceived(bill, jeb, Update(OtherVesselId, 1000));
        SendUnreliableUntilReceived(jeb, bill, Update(VesselId, 1000));
    }

    /// <summary>Descarta lo que haya llegado ya (por ejemplo, copias de los reintentos anteriores).</summary>
    private static void Drain(UdpClient udp)
    {
        Thread.Sleep(100);
        while (udp.Available > 0)
        {
            IPEndPoint? sender = null;
            udp.Receive(ref sender);
        }
    }

    /// <summary>Socket UDP en loopback que no se rompe con los avisos ICMP de "puerto inalcanzable" (Windows).</summary>
    private static UdpClient NewUdp()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        if (OperatingSystem.IsWindows())
            udp.Client.IOControl(-1744830452, new byte[4], null);
        return udp;
    }

    /// <summary>
    /// Manda el datagrama cada 100 ms hasta que <paramref name="to"/> recibe uno igual a <paramref name="expected"/>
    /// (o cualquiera, si es null). False si en el plazo no llega nada que valga.
    /// </summary>
    private static bool SendUntilReceived(UdpClient from, byte[] datagram, IPEndPoint target, UdpClient to, byte[]? expected, double seconds = 3)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            from.Send(datagram, target);
            DateTime step = DateTime.UtcNow.AddMilliseconds(100);
            while (DateTime.UtcNow < step)
            {
                if (to.Available == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                IPEndPoint? sender = null;
                byte[] received = to.Receive(ref sender);
                if (expected == null || received.AsSpan().SequenceEqual(expected))
                    return true;
            }
        }

        return false;
    }
}
