using System.Collections.Concurrent;
using System.Net;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;
using MultiKerbal.Server;

namespace MultiKerbal.Tests;

/// <summary>
/// Pruebas que miran lo que se escribe en el registro o cambian el idioma: los dos son estáticos y compartidos, así que
/// estas pruebas no corren a la vez que ninguna otra.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Registro e idioma (sin paralelo)";
}

[Collection(SerialCollection.Name)]
public sealed class LogAndLanguageTests
{
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Recoge las líneas del registro del relay mientras dura.</summary>
    private sealed class RelayLines : IDisposable
    {
        public RelayLines() => RelayLog.Listener = Lines.Enqueue;

        public ConcurrentQueue<string> Lines { get; } = new();

        public int Count(string text) => Lines.Count(l => l.Contains(text));

        public void Dispose() => RelayLog.Listener = null;
    }

    /// <summary>Recoge las líneas del registro del servidor mientras dura.</summary>
    private sealed class ServerLines : IDisposable
    {
        public ServerLines() => Log.Listener = Lines.Enqueue;

        public ConcurrentQueue<string> Lines { get; } = new();

        public int Count(string text) => Lines.Count(l => l.Contains(text));

        public void Dispose() => Log.Listener = null;
    }

    // --- Avisos del relay (I2) ---

    [Fact]
    public void RelayWarnings_FromOneIp_AreWrittenOncePerInterval()
    {
        using var lines = new RelayLines();
        using var relay = new TestRelay(LongLived);

        for (int i = 0; i < 3; i++)
        {
            using RawRelayClient client = relay.Connect();
            client.Send(new RelayHeartbeatMessage());
            Assert.True(client.WaitForClose());
        }

        Assert.Equal(1, lines.Count("primer mensaje no admitido"));
    }

    [Fact]
    public void WarningThrottle_CountsWhatItSilences_AndSummarizesOldEntries()
    {
        using var lines = new RelayLines();
        double now = 0;
        var throttle = new WarningThrottle(TimeSpan.FromSeconds(60), () => now);
        IPAddress a = IPAddress.Parse("198.51.100.1");
        IPAddress b = IPAddress.Parse("198.51.100.2");

        now = 10;
        throttle.Warn("prueba", a, "aviso A");
        now = 20;
        throttle.Warn("prueba", a, "aviso A"); // se calla
        now = 61;
        throttle.Warn("prueba", b, "aviso B"); // otra IP: sale (y el barrido no olvida A, que aún es reciente)
        Assert.Equal(1, lines.Count("aviso A"));
        Assert.Equal(1, lines.Count("aviso B"));

        // Pasado el intervalo, el siguiente aviso de A sale y dice cuántos se callaron.
        now = 75;
        throttle.Warn("prueba", a, "aviso A");
        Assert.Contains(lines.Lines, l => l.Contains("aviso A (y 1 aviso(s) igual(es) más antes de este)"));

        now = 80;
        throttle.Warn("prueba", b, "aviso B"); // se calla

        // B calló uno y nadie más avisó de B: al barrer las entradas viejas sale un resumen y se olvidan todas.
        now = 200;
        throttle.Warn("otra cosa", null, "aviso C");
        Assert.Contains(lines.Lines, l => l.Contains("1 aviso(s) más de tipo \"prueba\" desde 198.51.100.2"));
        Assert.Equal(1, lines.Count("aviso B"));
        Assert.Equal(1, lines.Count("aviso C"));
        Assert.Equal(1, throttle.TrackedCount);
    }

    // --- Registro diario del relay (N1) ---

    [Fact]
    public void RelayLog_StartsANewFileEachDay()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "relaylogs-" + Guid.NewGuid().ToString("N"));
        Func<DateTime> previousClock = RelayLog.Clock;
        var day = new DateTime(2026, 10, 14, 23, 59, 0);
        RelayLog.Clock = () => day;
        try
        {
            RelayLog.OpenDaily(directory);
            RelayLog.Info("antes de medianoche");
            day = day.AddMinutes(2);
            RelayLog.Info("después de medianoche");
        }
        finally
        {
            RelayLog.Close();
            RelayLog.Clock = previousClock;
        }

        Assert.Contains("antes de medianoche", File.ReadAllText(Path.Combine(directory, "relay-20261014.log")));
        string second = File.ReadAllText(Path.Combine(directory, "relay-20261015.log"));
        Assert.Contains("después de medianoche", second);
        Assert.DoesNotContain("antes de medianoche", second);
        Directory.Delete(directory, recursive: true);
    }

    // --- Registro del servidor (T9) ---

    [Fact]
    public void StatusQueriesAndControls_LeaveNoConnectionLines_ButPlayersDo()
    {
        using var lines = new ServerLines();
        using var server = new TestServer();

        for (int i = 0; i < 3; i++)
            Assert.NotNull(ServerStatusQuery.Query("127.0.0.1", server.Port, 3000, out _));
        Assert.True(ServerStatusQuery.SendControl("127.0.0.1", server.Port, ServerControlAction.SetPublic, 3000));
        Assert.True(TestRelay.WaitFor(() => server.Host.Connections.Length == 0, seconds: 3));
        Thread.Sleep(100); // el bucle principal procesa los cierres

        Assert.Equal(0, lines.Count("Conexión #"));

        server.Join("Jeb");
        Assert.Equal(1, lines.Count("Conexión #"));
        Assert.Equal(0, lines.Count("antes del saludo"));
    }

    [Fact]
    public void ConnectionThatSkipsTheHandshake_IsStillLogged()
    {
        using var lines = new ServerLines();
        using var server = new TestServer();

        using TestClient rude = TestClient.Connect(server.Port);
        rude.Send(new ChatMessage { Text = "hola sin saludar" });
        rude.WaitForEvent(NetEventType.Disconnected);

        // Se anota al llegar su primer mensaje y otra vez al cerrarse.
        Assert.True(TestRelay.WaitFor(() => lines.Count("antes del saludo") == 1, seconds: 3));
        Assert.Single(lines.Lines, l => l.Contains("Conexión #") && l.Contains(" desde "));
    }

    [Fact]
    public void ForeignControlWarning_IsWrittenOncePerMinute()
    {
        using var lines = new ServerLines();
        using var relay = new TestRelay(LongLived);
        using var server = new TestServer(relayAddress: relay.RelayAddress, reachableDirectly: false);
        string code = server.WaitForRelayCode();

        for (int i = 0; i < 2; i++)
        {
            using TestClient client = TestClient.StartWithCode(relay.RelayAddress, code);
            client.WaitForEvent(NetEventType.Connected);
            Assert.True(client.Net.ViaRelay);
            client.Send(new ServerControlMessage { Action = ServerControlAction.SetPublic });
            client.WaitForEvent(NetEventType.Disconnected);
        }

        Assert.False(server.Host.Config.Public);
        Assert.Equal(1, lines.Count("Orden del anfitrión ignorada"));
    }

    [Fact]
    public void WarningLimiter_LetsOneThroughPerInterval_AndCountsTheRest()
    {
        DateTime now = new(2026, 1, 1);
        var limiter = new WarningLimiter(TimeSpan.FromMinutes(1), () => now);

        Assert.True(limiter.Allow(out int skipped));
        Assert.Equal(0, skipped);
        Assert.False(limiter.Allow(out _));
        Assert.False(limiter.Allow(out _));

        now = now.AddSeconds(61);
        Assert.True(limiter.Allow(out skipped));
        Assert.Equal(2, skipped);
    }

    // --- Textos del relay en el idioma de cada uno (N2) ---

    [Fact]
    public void RelayErrors_AreDescribedInTheReadersLanguage()
    {
        bool previous = Lang.English;
        try
        {
            foreach (RelayError error in Enum.GetValues<RelayError>().Where(e => e is not RelayError.None and not RelayError.Other))
            {
                Lang.English = false;
                string spanish = RelayErrorText.Describe(error, "texto del relay");
                Lang.English = true;
                string english = RelayErrorText.Describe(error, "texto del relay");

                Assert.NotEqual("texto del relay", spanish);
                Assert.NotEqual(spanish, english);
            }

            // Un código que no se conoce (un relay más nuevo) usa el texto que manda el relay.
            Assert.Equal("texto del relay", RelayErrorText.Describe((RelayError)200, "texto del relay"));
            Assert.Equal("texto del relay", RelayErrorText.Describe(RelayError.Other, "texto del relay"));
            Assert.Equal(string.Empty, RelayErrorText.Describe((RelayError)200, null!));
        }
        finally
        {
            Lang.English = previous;
        }
    }

    [Fact]
    public async Task Player_SeesTheRelayErrorFromItsCode_NotTheRelaysText()
    {
        // Un relay falso que contesta a la consulta con un código conocido y un texto cualquiera.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task fake = Task.Run(() =>
        {
            using System.Net.Sockets.TcpClient tcp = listener.AcceptTcpClient();
            System.Net.Sockets.NetworkStream stream = tcp.GetStream();
            FrameCodec.ReadTcpFrame(stream, new byte[FrameCodec.TcpHeaderSize], out _);
            byte[] reply = FrameCodec.EncodeTcp(new RelayLookupResultMessage
            {
                Found = false,
                Name = string.Empty,
                ModVersion = string.Empty,
                DirectHost = string.Empty,
                ErrorCode = RelayError.ServerFull,
                Error = "texto del relay",
            });
            stream.Write(reply, 0, reply.Length);
            Thread.Sleep(300);
        });

        bool previous = Lang.English;
        Lang.English = true;
        try
        {
            using TestClient client = TestClient.StartWithCode($"127.0.0.1:{port}", "K7MQ2P");
            Assert.Equal("The server is full", client.WaitForEvent(NetEventType.Disconnected).Reason);
        }
        finally
        {
            Lang.English = previous;
            listener.Stop();
        }

        await fake.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
