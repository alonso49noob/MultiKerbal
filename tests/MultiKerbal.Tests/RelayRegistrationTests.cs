using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;

namespace MultiKerbal.Tests;

public sealed class RelayRegistrationTests
{
    // Los tiempos de prueba son cortos (caducidad de 1 s); casi todas las pruebas necesitan servidores que duren.
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private static RelayRegisteredMessage RegisterOk(RawRelayClient client, byte keySeed, string name = "Servidor de pruebas") =>
        client.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(keySeed), name));

    private static RelayLookupResultMessage Lookup(TestRelay relay, string code)
    {
        using RawRelayClient client = relay.Connect();
        return client.Request<RelayLookupResultMessage>(new RelayLookupMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = code });
    }

    [Fact]
    public void Register_GivesValidCode_AndLookupFindsTheServer()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient server = relay.Connect();

        RelayRegisteredMessage registered = RegisterOk(server, 1, "Mi servidor");

        Assert.True(ServerCode.TryNormalize(registered.Code, out string code));
        Assert.NotEqual(0UL, registered.UdpSecret);

        RelayLookupResultMessage result = Lookup(relay, ServerCode.Format(code));
        Assert.True(result.Found);
        Assert.Equal("Mi servidor", result.Name);
        Assert.Equal(ProtocolInfo.Version, result.GameProtocol);
        Assert.Equal("0.0.0-test", result.ModVersion);
        Assert.True(string.IsNullOrEmpty(result.DirectHost));
        Assert.Equal(1, relay.Host.LiveServerCount);
    }

    [Fact]
    public void Lookup_AcceptsTheCodeTheWayPlayersTypeIt()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient server = relay.Connect();
        string code = RegisterOk(server, 1).Code;

        Assert.True(Lookup(relay, code.ToLowerInvariant()).Found);
        Assert.True(Lookup(relay, ServerCode.Format(code)).Found);
    }

    [Fact]
    public void Lookup_UnknownCode_ReportsNotFound()
    {
        using var relay = new TestRelay(LongLived);

        RelayLookupResultMessage result = Lookup(relay, "K7M-Q2P");

        Assert.False(result.Found);
        Assert.Equal("No hay ningún servidor encendido con ese código", result.Error);
    }

    [Fact]
    public void Lookup_GarbageCode_ReportsNotFound()
    {
        using var relay = new TestRelay(LongLived);

        Assert.False(Lookup(relay, "gamepc").Found);
    }

    [Fact]
    public void Restart_KeepsTheCodeOfTheSameKey()
    {
        using var relay = new TestRelay(LongLived);
        string first;
        using (RawRelayClient server = relay.Connect())
            first = RegisterOk(server, 1).Code;

        relay.Restart();

        using RawRelayClient again = relay.Connect();
        Assert.Equal(first, RegisterOk(again, 1).Code);
    }

    [Fact]
    public void DifferentKeys_GetDifferentCodes()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient a = relay.Connect();
        using RawRelayClient b = relay.Connect();

        Assert.NotEqual(RegisterOk(a, 1).Code, RegisterOk(b, 2).Code);
        Assert.Equal(2, relay.Host.LiveServerCount);
    }

    [Fact]
    public void Register_SameKeyTwice_ReplacesTheOldConnection_AndKeepsTheCode()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient first = relay.Connect();
        string code = RegisterOk(first, 1, "Antes").Code;

        using RawRelayClient second = relay.Connect();
        Assert.Equal(code, RegisterOk(second, 1, "Después").Code);

        Assert.True(first.WaitForClose());
        Assert.Equal(1, relay.Host.LiveServerCount);

        // La sesión nueva sigue viva y es la que responde.
        RelayLookupResultMessage result = Lookup(relay, code);
        Assert.True(result.Found);
        Assert.Equal("Después", result.Name);
    }

    [Fact]
    public void Register_SixthServerFromTheSameIp_IsRejected()
    {
        using var relay = new TestRelay(LongLived);
        for (byte i = 1; i <= 5; i++)
            RegisterOk(relay.Connect(), (byte)(i * 10));

        using RawRelayClient sixth = relay.Connect();
        var rejected = sixth.Request<RelayRejectedMessage>(RawRelayClient.Register(RawRelayClient.Key(200)));
        Assert.False(string.IsNullOrEmpty(rejected.Reason));
        Assert.True(sixth.WaitForClose());
        Assert.Equal(5, relay.Host.LiveServerCount);

        // El rechazo no gasta un código: en el disco solo están los cinco servidores.
        Assert.Equal(5, new CodeStore(relay.DataDirectory).Count);

        // Volver a registrar una clave que ya está viva no cuenta como un servidor más.
        RegisterOk(relay.Connect(), 10);
        Assert.Equal(5, relay.Host.LiveServerCount);
    }

    [Fact]
    public void Register_WrongRelayVersion_IsRejected()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient server = relay.Connect();
        RelayRegisterMessage register = RawRelayClient.Register(RawRelayClient.Key(1));
        register.RelayVersion = ProtocolInfo.RelayVersion + 1;

        var rejected = server.Request<RelayRejectedMessage>(register);

        Assert.False(string.IsNullOrEmpty(rejected.Reason));
        Assert.True(server.WaitForClose());
        Assert.Equal(0, relay.Host.LiveServerCount);
    }

    [Fact]
    public void WithoutHeartbeats_TheServerDisappearsFromLookup()
    {
        using var relay = new TestRelay(); // caducidad de 1 s
        using RawRelayClient server = relay.Connect();
        string code = RegisterOk(server, 1).Code;

        Assert.True(TestRelay.WaitFor(() => relay.Host.LiveServerCount == 0, seconds: 2));
        Assert.False(Lookup(relay, code).Found);
        Assert.True(server.WaitForClose());
    }

    [Fact]
    public void Heartbeats_KeepTheServerAliveAndUpdateItsInfo()
    {
        using var relay = new TestRelay(); // caducidad de 1 s
        using RawRelayClient server = relay.Connect();
        string code = RegisterOk(server, 1, "Antes").Code;

        // Más de dos plazos seguidos, siempre con latidos.
        DateTime until = DateTime.UtcNow.AddSeconds(2.3);
        int beats = 0;
        while (DateTime.UtcNow < until)
        {
            server.Send(new RelayHeartbeatMessage
            {
                Info = new RelayServerInfo { Name = "Después", Players = ++beats, MaxPlayers = 8, GameProtocol = ProtocolInfo.Version, ModVersion = "0.0.0-test" },
            });
            Thread.Sleep(200);
        }

        RelayLookupResultMessage result = Lookup(relay, code);
        Assert.True(result.Found);
        Assert.Equal("Después", result.Name);
        Assert.True(TestRelay.WaitFor(() => relay.Host.TryGetServer(code, out ServerSession? s) && s.Info.Players == beats));
    }

    [Fact]
    public void BanCommand_ClosesTheSession_AndRejectsTheKeyAfterwards()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient server = relay.Connect();
        string code = RegisterOk(server, 1).Code;

        relay.Host.ExecuteCommand($"ban {ServerCode.Format(code)}");

        // La consulta ya no lo encuentra en cuanto vuelve el comando, sin esperar a que se cierre la lectura.
        Assert.Equal(0, relay.Host.LiveServerCount);
        Assert.False(Lookup(relay, code).Found);
        Assert.True(server.WaitForClose());

        using RawRelayClient again = relay.Connect();
        again.Request<RelayRejectedMessage>(RawRelayClient.Register(RawRelayClient.Key(1)));

        // Con unban la misma clave vuelve a entrar y conserva el código.
        relay.Host.ExecuteCommand($"unban {code}");
        Assert.Equal(code, RegisterOk(relay.Connect(), 1).Code);
    }

    [Fact]
    public void Ban_SurvivesARestart()
    {
        using var relay = new TestRelay(LongLived);
        string code;
        using (RawRelayClient server = relay.Connect())
            code = RegisterOk(server, 1).Code;
        relay.Host.ExecuteCommand($"ban {code}");

        relay.Restart();

        using RawRelayClient again = relay.Connect();
        again.Request<RelayRejectedMessage>(RawRelayClient.Register(RawRelayClient.Key(1)));
    }

    [Fact]
    public void ListAndUnknownCommands_DoNotThrow()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient server = relay.Connect();
        RegisterOk(server, 1);

        relay.Host.ExecuteCommand("list");
        relay.Host.ExecuteCommand("help");
        relay.Host.ExecuteCommand("ban");
        relay.Host.ExecuteCommand("ban ZZZZZZ");
        relay.Host.ExecuteCommand("unban nada");
        relay.Host.ExecuteCommand("cosa rara");
        relay.Host.ExecuteCommand(string.Empty);

        Assert.Equal(1, relay.Host.LiveServerCount);
    }

    [Fact]
    public void SilentConnection_IsClosedAtTheFirstMessageDeadline()
    {
        using var relay = new TestRelay(o => o.FirstMessageTimeout = TimeSpan.FromSeconds(1));
        using RawRelayClient silent = relay.Connect();

        Assert.True(silent.WaitForClose(seconds: 3));
    }

    // Cabecera que anuncia 1 MB (más que los 64 KB del relay); no se manda el resto. Si el relay no mirase la
    // longitud antes de leer, se quedaría esperando el cuerpo y la conexión no se cerraría en el plazo de la prueba.
    private static byte[] OversizedHeader() =>
        BitConverter.GetBytes(1024 * 1024 + 2).Concat(BitConverter.GetBytes((ushort)MessageType.RelayLookup)).ToArray();

    [Fact]
    public void OversizedFirstMessage_ClosesTheConnectionWithoutReadingIt()
    {
        // Plazo del primer mensaje muy largo: si se cierra es por el tamaño, no por el plazo.
        using var relay = new TestRelay(o => o.FirstMessageTimeout = TimeSpan.FromSeconds(30));
        using RawRelayClient client = relay.Connect();

        client.SendRaw(OversizedHeader());

        Assert.True(client.WaitForClose(seconds: 1));
    }

    [Fact]
    public void OversizedMessageOnTheControl_ClosesAndRemovesTheSession()
    {
        // Latido muy largo: si se cierra es por el tamaño, no por la caducidad.
        using var relay = new TestRelay(LongLived);
        using RawRelayClient server = relay.Connect();
        RegisterOk(server, 1);

        server.SendRaw(OversizedHeader());

        Assert.True(server.WaitForClose(seconds: 1));
        Assert.True(TestRelay.WaitFor(() => relay.Host.LiveServerCount == 0, seconds: 1));
    }

    [Fact]
    public void UnsupportedFirstMessage_ClosesTheConnection()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient client = relay.Connect();

        client.Send(new RelayHeartbeatMessage());

        Assert.True(client.WaitForClose());
    }

    [Fact]
    public void Restart_ClosesLiveSessionsAndPendingConnections()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient server = relay.Connect();
        RegisterOk(server, 1);
        using RawRelayClient silent = relay.Connect();

        relay.Restart();

        Assert.True(server.WaitForClose());
        Assert.True(silent.WaitForClose());
        Assert.Equal(0, relay.Host.LiveServerCount);
    }
}
