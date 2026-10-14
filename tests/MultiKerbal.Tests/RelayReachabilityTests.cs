using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Relay;
using MultiKerbal.Server.Relay;

namespace MultiKerbal.Tests;

public sealed class RelayReachabilityTests
{
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private static RelayLink LinkOf(TestServer server) => server.Host.RelayLink ?? throw new InvalidOperationException("El servidor no usa el relay");

    private static RelayLookupResultMessage Lookup(TestRelay relay, string code)
    {
        using RawRelayClient client = relay.Connect();
        return client.Request<RelayLookupResultMessage>(new RelayLookupMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = code });
    }

    [Fact]
    public void LoopbackServer_IsDirect_AndPlayersWithTheCodeConnectDirectly()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        string code = server.WaitForRelayCode();

        Assert.True(TestRelay.WaitFor(() => LinkOf(server).Direct, seconds: 3), "El servidor no quedó como accesible directamente");
        Assert.True(relay.Host.TryGetServer(code, out ServerSession? session));
        Assert.True(session.DirectTcp);
        Assert.True(session.DirectUdp);
        RelayLookupResultMessage lookup = Lookup(relay, code);
        Assert.Equal("127.0.0.1", lookup.DirectHost);
        Assert.Equal(server.Port, lookup.DirectPort);

        TestClient jeb = server.Track(TestClient.JoinWithCode(relay.RelayAddress, code, "Jeb"));

        Assert.False(jeb.Net.ViaRelay);
        Assert.Equal("Servidor de pruebas", jeb.Net.ServerName);
        Assert.Equal(0, relay.Host.RelaySessionCount);
    }

    [Fact]
    public void ServerAnnouncingAClosedPort_IsNotDirect_AndPlayersEnterThroughTheRelay()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        string code = server.WaitForRelayCode();

        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount >= 1, seconds: 3), "La comprobación no terminó");
        Assert.True(relay.Host.TryGetServer(code, out ServerSession? session));
        Assert.False(session.DirectTcp);
        Assert.False(session.DirectUdp);
        Assert.False(LinkOf(server).Direct);
        Assert.True(string.IsNullOrEmpty(Lookup(relay, code).DirectHost));

        TestClient jeb = server.Track(TestClient.JoinWithCode(relay.RelayAddress, code, "Jeb"));

        Assert.True(jeb.Net.ViaRelay);
        Assert.Equal(1, relay.Host.RelaySessionCount);
    }

    [Fact]
    public void PlanWithAClosedDirectAddress_FallsBackToTheRelay()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        string code = server.WaitForRelayCode();

        var plan = new ConnectPlan
        {
            Host = "127.0.0.1",
            Port = TestServer.ReserveFreePort(),
            RelayHost = "127.0.0.1",
            RelayPort = relay.Port,
            JoinCode = code,
            DirectTimeoutMs = 1000,
        };
        TestClient jeb = server.Track(TestClient.JoinWithPlan(plan, "Jeb"));

        Assert.True(jeb.Net.ViaRelay);
        Assert.True(jeb.Welcome!.Accepted);
    }

    [Fact]
    public void Recheck_StartsANewProbe()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        server.WaitForRelayCode();
        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount >= 1, seconds: 3));
        int before = relay.Host.ProbeCount;

        LinkOf(server).Recheck();

        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount > before, seconds: 3));
    }

    [Fact]
    public void Probes_RepeatEveryRecheckInterval()
    {
        using var relay = new TestRelay(o =>
        {
            o.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            o.RecheckInterval = TimeSpan.FromMilliseconds(200);
        });
        using var server = new TestServer(relayAddress: relay.RelayAddress);
        server.WaitForRelayCode();

        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount >= 3, seconds: 3));
        Assert.True(LinkOf(server).Direct);
    }
}
