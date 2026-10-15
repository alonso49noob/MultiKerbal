using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;
using MultiKerbal.Server;

namespace MultiKerbal.Tests;

public sealed class PublicListTests
{
    // El relay de pruebas caduca a 1 s; aquí los servidores tienen que durar.
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private static PublicServerEntry[] List(TestRelay relay) => RelayDirectory.List(relay.RelayAddress, 3000);

    private static bool IsListed(TestRelay relay, string code) => List(relay).Any(e => e.Code == code);

    /// <summary>Registra un servidor "a mano" en el relay y devuelve su código. La conexión tiene que seguir abierta para que siga vivo.</summary>
    private static string RegisterRaw(RawRelayClient client, int id, string name, int players = 0, bool isPublic = true, bool hasPassword = false)
    {
        var key = new byte[32];
        key[0] = (byte)id;
        key[1] = (byte)(id >> 8);
        key[2] = 0x5A;
        RelayRegisterMessage register = RawRelayClient.Register(key, name);
        register.Info.Players = players;
        register.Info.IsPublic = isPublic;
        register.Info.HasPassword = hasPassword;
        return client.Request<RelayRegisteredMessage>(register).Code;
    }

    [Fact]
    public void PrivateServer_IsNotListed_ButAppearsRightAfterPublicOn_AndDisappearsAfterPublicOff()
    {
        using var relay = new TestRelay(LongLived);

        // Latido de 30 s: solo el latido forzado por el comando puede hacer que el cambio llegue al relay a tiempo.
        using var server = new TestServer(relayAddress: relay.RelayAddress, relayHeartbeat: TimeSpan.FromSeconds(30));
        string code = server.WaitForRelayCode();

        Assert.Empty(List(relay));

        server.Host.EnqueueCommand("public on");
        Assert.True(TestRelay.WaitFor(() => IsListed(relay, code), seconds: 1), "El servidor no apareció en menos de 1 s");

        server.Host.EnqueueCommand("public off");
        Assert.True(TestRelay.WaitFor(() => !IsListed(relay, code), seconds: 1), "El servidor no desapareció en menos de 1 s");
    }

    [Fact]
    public void PublicServer_IsListedWithItsData()
    {
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, isPublic: true, serverName: "Base Lunar");
        string code = server.WaitForRelayCode();

        PublicServerEntry entry = Assert.Single(List(relay));

        Assert.Equal(code, entry.Code);
        Assert.DoesNotContain('-', entry.Code);
        Assert.Equal("Base Lunar", entry.Name);
        Assert.Equal(0, entry.Players);
        Assert.Equal(8, entry.MaxPlayers);
        Assert.False(entry.HasPassword);
        Assert.Equal(ProtocolInfo.Version, entry.GameProtocol);
        Assert.False(string.IsNullOrEmpty(entry.ModVersion));
    }

    [Fact]
    public void List_ReportsDirectAccordingToTheReachabilityCheck()
    {
        using var relay = new TestRelay(LongLived);
        using var reachable = new TestServer(relayAddress: relay.RelayAddress, isPublic: true, serverName: "Directo");
        using var behindNat = new TestServer(relayAddress: relay.RelayAddress, isPublic: true, serverName: "Por el relay", reachableDirectly: false);
        string directCode = reachable.WaitForRelayCode();
        string relayedCode = behindNat.WaitForRelayCode();

        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount >= 2, seconds: 4), "El relay no terminó las comprobaciones");
        Assert.True(TestRelay.WaitFor(() => List(relay).Any(e => e.Code == directCode && e.Direct), seconds: 2));

        Assert.False(List(relay).Single(e => e.Code == relayedCode).Direct);
    }

    [Fact]
    public void List_IsSortedByPlayersThenByName()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient a = relay.Connect();
        using RawRelayClient b = relay.Connect();
        using RawRelayClient c = relay.Connect();
        using RawRelayClient d = relay.Connect();
        RegisterRaw(a, 1, "beta", players: 0);
        RegisterRaw(b, 2, "Zeta", players: 2);
        RegisterRaw(c, 3, "Alfa", players: 0);
        RegisterRaw(d, 4, "Medio", players: 1);

        string[] names = List(relay).Select(e => e.Name).ToArray();

        // Más jugadores primero; a igualdad, por nombre sin distinguir mayúsculas.
        Assert.Equal(new[] { "Zeta", "Medio", "Alfa", "beta" }, names);
    }

    [Fact]
    public void List_OnlyIncludesPublicServers_AndKeepsThePasswordFlag()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient secret = relay.Connect();
        using RawRelayClient open = relay.Connect();
        using RawRelayClient locked = relay.Connect();
        RegisterRaw(secret, 1, "Secreto", isPublic: false);
        RegisterRaw(open, 2, "Abierto");
        RegisterRaw(locked, 3, "Con clave", hasPassword: true);

        PublicServerEntry[] entries = List(relay);

        Assert.Equal(2, entries.Length);
        Assert.DoesNotContain(entries, e => e.Name == "Secreto");
        Assert.False(entries.Single(e => e.Name == "Abierto").HasPassword);
        Assert.True(entries.Single(e => e.Name == "Con clave").HasPassword);
    }

    [Fact]
    public void List_DropsServersThatWentAway()
    {
        using var relay = new TestRelay(LongLived);
        RawRelayClient gone = relay.Connect();
        RegisterRaw(gone, 1, "Se va");
        using RawRelayClient stays = relay.Connect();
        RegisterRaw(stays, 2, "Se queda");
        Assert.Equal(2, List(relay).Length);

        gone.Dispose();

        Assert.True(TestRelay.WaitFor(() => List(relay).Length == 1, seconds: 3));
        Assert.Equal("Se queda", List(relay).Single().Name);
    }

    [Fact]
    public void HostileNames_ArriveCutTo40_WithoutControlCharacters()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient hostile = relay.Connect();
        using RawRelayClient padded = relay.Connect();
        using RawRelayClient controls = relay.Connect();
        RegisterRaw(hostile, 1, "Base\n<color=red>Lunar</color>" + new string('x', 500), players: 3);
        RegisterRaw(padded, 2, "   Hola   ", players: 2);
        RegisterRaw(controls, 3, "A\r\nB\tC\u0007D\0E", players: 1);

        PublicServerEntry[] entries = List(relay);

        Assert.Equal(3, entries.Length);
        Assert.Equal("Base<color=red>Lunar</color>" + new string('x', 12), entries[0].Name);
        Assert.Equal(ProtocolInfo.MaxListedNameLength, entries[0].Name.Length);
        Assert.Equal("Hola", entries[1].Name);
        Assert.Equal("ABCDE", entries[2].Name);
        Assert.All(entries, e => Assert.DoesNotContain(e.Name, ch => char.IsControl(ch)));
    }

    [Fact]
    public void List_NeverHasMoreThan500Servers_AndKeepsTheBestOnes()
    {
        using var relay = new TestRelay(o =>
        {
            o.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            o.MaxServersPerIp = 1000;
        });
        int total = ProtocolInfo.MaxListedServers + 3;
        for (int i = 0; i < total; i++)
            RegisterRaw(relay.Connect(), i, "S" + i, players: i % 7);
        Assert.Equal(total, relay.Host.LiveServerCount);

        PublicServerEntry[] entries = List(relay);

        Assert.Equal(ProtocolInfo.MaxListedServers, entries.Length);
        Assert.Equal(6, entries[0].Players);
        Assert.Equal(entries.Select(e => e.Players).OrderByDescending(p => p), entries.Select(e => e.Players));
    }

    [Fact]
    public void ListRequest_WithAnotherRelayVersion_IsClosedWithoutAnswer()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient client = relay.Connect();

        client.Send(new RelayListRequestMessage { RelayVersion = ProtocolInfo.RelayVersion + 1 });

        Assert.True(client.WaitForClose());
    }

    [Fact]
    public void ServerNames_AreSanitizedInTheRelayLog()
    {
        string logPath = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "relaylog-" + Guid.NewGuid().ToString("N") + ".log");
        RelayLog.OpenFile(logPath);
        try
        {
            using var relay = new TestRelay(LongLived);
            using RawRelayClient client = relay.Connect();
            RegisterRaw(client, 1, "Falso\n[12:00:00] [ERROR] linea inventada");
            Assert.True(TestRelay.WaitFor(() => relay.Host.LiveServerCount == 1));
        }
        finally
        {
            RelayLog.Close();
        }

        string[] lines = File.ReadAllLines(logPath);
        Assert.Contains(lines, l => l.Contains("Servidor registrado") && l.Contains("Falso[12:00:00] [ERROR] linea inventada"));
        Assert.DoesNotContain(lines, l => l.StartsWith("[12:00:00]"));
        File.Delete(logPath);
    }

    [Fact]
    public void Sanitize_RemovesControlCharacters_TrimsAndCuts()
    {
        Assert.Equal("Hola mundo", ListedName.Sanitize("  Hola\n mundo\t "));
        Assert.Equal(string.Empty, ListedName.Sanitize(null));
        Assert.Equal(string.Empty, ListedName.Sanitize("\n\r\t"));
        Assert.Equal(new string('a', 40), ListedName.Sanitize(new string('a', 100)));

        // Si el corte deja un espacio al final, también se quita.
        Assert.Equal(new string('a', 39), ListedName.Sanitize(new string('a', 39) + " bbb"));

        // Un par suplente (emoji) no se parte por la mitad.
        string cut = ListedName.Sanitize(new string('a', 39) + "\U0001F680");
        Assert.Equal(new string('a', 39), cut);
        Assert.DoesNotContain(cut, char.IsSurrogate);
    }

    [Fact]
    public void Directory_WithoutRelay_ThrowsIOException()
    {
        int closedPort = TestServer.ReserveFreePort();

        Assert.Throws<IOException>(() => RelayDirectory.List($"127.0.0.1:{closedPort}", 1000));
        Assert.Throws<IOException>(() => RelayDirectory.List("no es una dirección", 1000));
    }

    [Fact]
    public void Directory_WithSilentRelay_ThrowsIOExceptionOnTimeout()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            DateTime start = DateTime.UtcNow;

            Assert.Throws<IOException>(() => RelayDirectory.List($"127.0.0.1:{port}", 500));

            Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(3));
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public void PublicCommand_SavesServerJson_WhenThereIsAPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "server-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var server = new TestServer();
            server.Host.ConfigPath = path;
            Assert.False(server.Host.Config.Public);

            server.Host.EnqueueCommand("public on");

            Assert.True(TestRelay.WaitFor(() => File.Exists(path) && File.ReadAllText(path).Contains("\"Public\": true")), "No se guardó server.json");
            Assert.True(server.Host.Config.Public);
            Assert.True(ServerConfig.LoadOrCreate(path).Public);

            server.Host.EnqueueCommand("public off");

            Assert.True(TestRelay.WaitFor(() => File.ReadAllText(path).Contains("\"Public\": false")));
            Assert.False(ServerConfig.LoadOrCreate(path).Public);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Servidor de pruebas", document.RootElement.GetProperty("ServerName").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PublicCommand_WithoutPath_ChangesOnlyTheRunningConfig()
    {
        using var server = new TestServer();
        Assert.Null(server.Host.ConfigPath);

        server.Host.EnqueueCommand("public on");

        Assert.True(TestRelay.WaitFor(() => server.Host.Config.Public));
    }
}
