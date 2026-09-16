using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Server;

namespace MultiKerbal.Tests;

public class HandshakeValidationTests
{
    private static HandshakeRequestMessage Request(string name, int version = ProtocolInfo.Version, string? password = null) =>
        new() { ProtocolVersion = version, PlayerName = name, Password = password };

    [Fact]
    public void ValidRequest_IsAccepted()
    {
        Assert.Null(ServerHost.ValidateHandshake(Request("Jebediah Kerman"), new ServerConfig(), []));
    }

    [Fact]
    public void ProtocolMismatch_IsRejected()
    {
        Assert.Contains("protocolo", ServerHost.ValidateHandshake(Request("Jeb", version: 999), new ServerConfig(), []));
    }

    [Theory]
    [InlineData("J")]
    [InlineData("   ")]
    [InlineData("Nombre_demasiado_largo_para_ksp")]
    [InlineData("<color=red>Jeb</color>")]
    [InlineData("Jeb\nKerman")]
    public void InvalidNames_AreRejected(string name)
    {
        Assert.NotNull(ServerHost.ValidateHandshake(Request(name), new ServerConfig(), []));
    }

    [Fact]
    public void DuplicateName_IsRejectedIgnoringCase()
    {
        Assert.Contains("nombre", ServerHost.ValidateHandshake(Request("jeb"), new ServerConfig(), ["Jeb"]));
    }

    [Fact]
    public void Password_IsEnforced()
    {
        var config = new ServerConfig { Password = "kraken" };

        Assert.Contains("Contraseña", ServerHost.ValidateHandshake(Request("Jeb", password: "mal"), config, []));
        Assert.Null(ServerHost.ValidateHandshake(Request("Jeb", password: "kraken"), config, []));
    }

    [Fact]
    public void FullServer_IsRejected()
    {
        Assert.Contains("lleno", ServerHost.ValidateHandshake(Request("Val"), new ServerConfig { MaxPlayers = 2 }, ["Jeb", "Bill"]));
    }

    [Fact]
    public void SanitizeText_StripsControlCharsTrimsAndTruncates()
    {
        Assert.Equal("hola mundo", ServerHost.SanitizeText("  hola mundo\r\n ", 100));
        Assert.Equal("abc", ServerHost.SanitizeText("abcdef", 3));
        Assert.Equal(string.Empty, ServerHost.SanitizeText(null, 10));
    }
}

public sealed class ServerIntegrationTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", Guid.NewGuid().ToString("N"));
    private readonly List<TestClient> _clients = new();
    private ServerHost _server;

    public ServerIntegrationTests()
    {
        Log.ConsoleEnabled = false;
        _server = StartServer();
    }

    [Fact]
    public void Join_SendsWelcomePlayerListAndClock()
    {
        TestClient jeb = Track(TestClient.Join(_server.Port, "Jeb"));

        Assert.Equal("Servidor de pruebas", jeb.Welcome!.ServerName);
        Assert.NotEqual(0UL, jeb.Welcome.UdpToken);
        jeb.WaitFor<PlayerListMessage>(m => m.Players.Any(p => p.Name == "Jeb" && p.Id == jeb.Welcome.PlayerId));
        Assert.Equal(1.0, jeb.WaitFor<TimeStateMessage>().Rate);
    }

    [Fact]
    public void SecondPlayer_IsAnnounced_AndChatReachesEveryone()
    {
        TestClient jeb = Track(TestClient.Join(_server.Port, "Jeb"));
        TestClient bill = Track(TestClient.Join(_server.Port, "Bill"));

        jeb.WaitFor<PlayerJoinedMessage>(m => m.Player.Name == "Bill");
        bill.Send(new ChatMessage { SenderId = 999, Text = "  ¿acoplamos? " });

        ChatMessage received = jeb.WaitFor<ChatMessage>(m => m.SenderId != ChatMessage.SystemSenderId);
        Assert.Equal(bill.Welcome!.PlayerId, received.SenderId); // el servidor ignora el remitente que dice ser
        Assert.Equal("¿acoplamos?", received.Text);
        bill.WaitFor<ChatMessage>(m => m.Text == "¿acoplamos?");
    }

    [Fact]
    public void DuplicateName_GetsRejectionAndDisconnect()
    {
        Track(TestClient.Join(_server.Port, "Jeb"));
        TestClient impostor = Track(TestClient.Connect(_server.Port));

        impostor.Send(new HandshakeRequestMessage { ProtocolVersion = ProtocolInfo.Version, PlayerName = "JEB" });

        Assert.False(impostor.WaitFor<HandshakeResponseMessage>().Accepted);
        Assert.Contains("nombre", impostor.WaitForEvent(NetEventType.Disconnected).Reason);
    }

    [Fact]
    public void MessagesBeforeHandshake_GetTheClientKicked()
    {
        TestClient rude = Track(TestClient.Connect(_server.Port));

        rude.Send(new ChatMessage { Text = "hola" });

        Assert.Contains("saludo", rude.WaitForEvent(NetEventType.Disconnected).Reason);
    }

    [Fact]
    public void Ping_IsAnsweredWithServerTime()
    {
        TestClient jeb = Track(TestClient.Join(_server.Port, "Jeb"));

        jeb.Send(new PingMessage { ClientTime = 42.5 });

        PongMessage pong = jeb.WaitFor<PongMessage>();
        Assert.Equal(42.5, pong.ClientTime);
        Assert.True(pong.ServerTime > 0);
    }

    [Fact]
    public void Udp_BindsAndCarriesUnreliableMessages()
    {
        TestClient jeb = Track(TestClient.Join(_server.Port, "Jeb"));

        jeb.Net.StartUdp(jeb.Welcome!.UdpToken);
        TestClient.WaitUntil(() => jeb.Net.UdpReady);
        jeb.Send(new WarpRequestMessage { Participating = true, Rate = 5, Mode = WarpMode.Rails }, Delivery.Unreliable);

        jeb.WaitFor<TimeStateMessage>(m => m.Rate == 5);
    }

    [Fact]
    public void Warp_FollowsSlowestPlayer()
    {
        TestClient jeb = Track(TestClient.Join(_server.Port, "Jeb"));
        TestClient bill = Track(TestClient.Join(_server.Port, "Bill"));

        jeb.Send(new WarpRequestMessage { Participating = true, Rate = 1000 });
        bill.Send(new WarpRequestMessage { Participating = true, Rate = 10 });
        jeb.WaitFor<TimeStateMessage>(m => m.Rate == 10 && m.LimitedBy == "Bill");

        bill.Send(new WarpRequestMessage { Participating = true, Rate = 1000 });
        jeb.WaitFor<TimeStateMessage>(m => m.Rate == 1000 && m.LimitedBy == "");

        bill.Net.Disconnect("Me voy");
        jeb.WaitFor<PlayerLeftMessage>(m => m.PlayerId == bill.Welcome!.PlayerId);
    }

    [Fact]
    public void Clock_PausesWhenEmpty_AndPersistsAcrossRestarts()
    {
        TestClient jeb = Track(TestClient.Join(_server.Port, "Jeb"));
        jeb.WaitFor<TimeStateMessage>();
        Thread.Sleep(300);
        jeb.Net.Disconnect("Adiós");
        jeb.WaitForEvent(NetEventType.Disconnected);
        _server.Stop();

        _server = StartServer();
        TestClient again = Track(TestClient.Join(_server.Port, "Jeb"));
        TimeStateMessage state = again.WaitFor<TimeStateMessage>();

        Assert.InRange(state.UniversalTime, 0.25, 5.0);
    }

    public void Dispose()
    {
        foreach (TestClient client in _clients)
            client.Dispose();
        _server.Stop();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ServerHost StartServer()
    {
        var server = new ServerHost(new ServerConfig
        {
            Port = 0,
            DataDirectory = _dataDirectory,
            ServerName = "Servidor de pruebas",
        });
        server.Start();
        return server;
    }

    private TestClient Track(TestClient client)
    {
        _clients.Add(client);
        return client;
    }
}
