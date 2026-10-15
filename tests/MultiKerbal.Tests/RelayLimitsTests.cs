using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;

namespace MultiKerbal.Tests;

/// <summary>Límites del relay contra el abuso: claves nuevas, conexiones y sesiones por IP, tamaño del primer mensaje...</summary>
public sealed class RelayLimitsTests
{
    private static readonly Action<RelayOptions> LongLived = o => o.HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private static RelayRejectedMessage Reject(TestRelay relay, byte keySeed)
    {
        using RawRelayClient client = relay.Connect();
        return client.Request<RelayRejectedMessage>(RawRelayClient.Register(RawRelayClient.Key(keySeed)));
    }

    // --- Claves nuevas por IP (I1) ---

    [Fact]
    public void NewKeys_FromOneIp_AreLimited_ButKnownKeysRegisterFreely()
    {
        using var relay = new TestRelay(o =>
        {
            LongLived(o);
            o.MaxNewCodesPerIp = 2;
        });
        RawRelayClient first = relay.Connect();
        string code = first.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(1))).Code;
        relay.Connect().Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(2)));

        RelayRejectedMessage rejected = Reject(relay, 3);

        Assert.Equal(RelayError.TooManyNewCodes, rejected.ErrorCode);
        Assert.False(rejected.Permanent);
        Assert.False(string.IsNullOrEmpty(rejected.Reason));
        Assert.Equal(2, new CodeStore(relay.DataDirectory).Count);

        // Una clave que ya tiene código no gasta nada: vuelve a entrar aunque la IP no pueda pedir más.
        first.Dispose();
        var again = relay.Connect().Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(1)));
        Assert.Equal(code, again.Code);
    }

    [Fact]
    public void NewCode_ThatCannotBeSaved_IsRejectedForNow_AndIsNotKept()
    {
        using var relay = new TestRelay(LongLived);

        // Una carpeta donde va el archivo temporal: escribir codes.json falla.
        string blocker = Path.Combine(relay.DataDirectory, "codes.json.tmp");
        Directory.CreateDirectory(blocker);

        RelayRejectedMessage rejected = Reject(relay, 7);
        Assert.Equal(RelayError.CodeNotSaved, rejected.ErrorCode);
        Assert.False(rejected.Permanent);
        Assert.Equal(0, relay.Host.LiveServerCount);

        // El código no se quedó a medias: con el disco ya bien, la misma clave entra y se guarda.
        Directory.Delete(blocker);
        using RawRelayClient client = relay.Connect();
        string code = client.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(7))).Code;
        Assert.True(relay.Host.TryGetServer(code, out _));
        Assert.Equal(1, new CodeStore(relay.DataDirectory).Count);
    }

    [Fact]
    public void NewCodeBudget_RefillsAfterTheWindow_AndForgetsIdleAddresses()
    {
        double now = 0;
        var budget = new NewCodeBudget(2, TimeSpan.FromSeconds(100), () => now);
        IPAddress a = IPAddress.Parse("198.51.100.1");

        Assert.True(budget.TryTake(a));
        now = 10;
        Assert.True(budget.TryTake(a));
        Assert.False(budget.TryTake(a));

        now = 100; // la primera ya salió de la ventana
        Assert.True(budget.TryTake(a));
        Assert.False(budget.TryTake(a));

        for (int i = 0; i < 50; i++)
            budget.TryTake(IPAddress.Parse($"203.0.113.{i}"));
        Assert.Equal(51, budget.TrackedCount);

        now = 1000;
        budget.TryTake(IPAddress.Parse("192.0.2.1"));
        Assert.Equal(1, budget.TrackedCount);
    }

    // --- Conexiones por IP (I2) ---

    [Fact]
    public void ConnectionsFromOneIp_AreCapped_ButServerLinksDoNotCount()
    {
        using var relay = new TestRelay(o =>
        {
            LongLived(o);
            o.FirstMessageTimeout = TimeSpan.FromSeconds(30);
            o.MaxConnectionsPerIp = 2;
        });

        // El control de un servidor deja de contar en cuanto se registra.
        RawRelayClient control = relay.Connect();
        control.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(1)));

        RawRelayClient silent1 = relay.Connect();
        RawRelayClient silent2 = relay.Connect();
        RawRelayClient third = relay.Connect();

        Assert.True(third.WaitForClose(seconds: 2), "La tercera conexión a la vez debía cerrarse");
        Assert.False(silent1.WaitForClose(seconds: 0.3));
        Assert.False(silent2.WaitForClose(seconds: 0.1));

        // Al cerrarse una, hay sitio para otra.
        silent1.Dispose();
        Thread.Sleep(200);
        RawRelayClient fourth = relay.Connect();
        Assert.False(fourth.WaitForClose(seconds: 0.3));
    }

    [Fact]
    public void NewConnectionsFromOneIp_AreRateLimited()
    {
        using var relay = new TestRelay(o =>
        {
            LongLived(o);
            o.NewConnectionsPerSecondPerIp = 0.5;
            o.NewConnectionBurstPerIp = 3;
        });

        // Tres de golpe pasan (la ráfaga) y contestan; la cuarta se cierra sin leer nada.
        for (int i = 0; i < 3; i++)
        {
            using RawRelayClient client = relay.Connect();
            Assert.IsType<RelayListResponseMessage>(client.Request<RelayListResponseMessage>(new RelayListRequestMessage { RelayVersion = ProtocolInfo.RelayVersion }));
        }

        using RawRelayClient fourth = relay.Connect();
        Assert.True(fourth.WaitForClose(seconds: 2));
    }

    [Fact]
    public void ConnectionGate_ForgetsIdleAddresses()
    {
        double now = 0;
        var options = new RelayOptions { MaxConnectionsPerIp = 2, NewConnectionsPerSecondPerIp = 1, NewConnectionBurstPerIp = 2 };
        var gate = new ConnectionGate(options, () => now);
        IPAddress busy = IPAddress.Parse("198.51.100.7");

        Assert.True(gate.TryEnter(busy, out _));
        Assert.True(gate.TryEnter(busy, out _));
        Assert.False(gate.TryEnter(busy, out string full));
        Assert.Contains("abiertas", full);

        gate.Leave(busy);
        Assert.False(gate.TryEnter(busy, out string fast)); // hay sitio, pero el cubo está vacío
        Assert.Contains("deprisa", fast);
        now = 1;
        Assert.True(gate.TryEnter(busy, out _));

        for (int i = 0; i < 100; i++)
        {
            IPAddress address = IPAddress.Parse($"203.0.113.{i}");
            Assert.True(gate.TryEnter(address, out _));
            gate.Leave(address);
        }

        Assert.Equal(101, gate.TrackedCount);

        // Pasado el barrido, solo queda la que aún tiene conexiones abiertas.
        now = 120;
        gate.TryEnter(busy, out _);
        Assert.Equal(1, gate.TrackedCount);
    }

    [Fact]
    public void OneIp_CannotTakeEverySessionOfAServer()
    {
        using var relay = new TestRelay(o =>
        {
            LongLived(o);
            o.MaxSessionsPerPlayerIp = 2;
        });
        RawRelayClient control = relay.Connect();
        string code = control.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(1))).Code;

        // Dos sesiones esperando el enganche (el servidor en bruto no se engancha nunca).
        for (int i = 0; i < 2; i++)
        {
            relay.Connect().Send(new RelayJoinMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = code });
            control.ReceiveControl<RelayIncomingMessage>();
        }

        using RawRelayClient third = relay.Connect();
        var result = third.Request<RelayJoinResultMessage>(new RelayJoinMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = code });

        Assert.False(result.Ok);
        Assert.Equal(RelayError.TooManySessionsFromIp, result.ErrorCode);
        Assert.Equal(2, relay.Host.RelaySessionCount);
    }

    // --- Tamaño del primer mensaje (I2) y textos del servidor (T8) ---

    [Fact]
    public void FirstMessage_LargerThanTheFirstMessageLimit_ClosesTheConnection()
    {
        using var relay = new TestRelay(o => o.FirstMessageTimeout = TimeSpan.FromSeconds(30));
        using RawRelayClient client = relay.Connect();

        // 5 KB: por debajo del límite general (64 KB), por encima del del primer mensaje (4 KB). Sin cuerpo.
        client.SendRaw(BitConverter.GetBytes(5 * 1024 + 2).Concat(BitConverter.GetBytes((ushort)MessageType.RelayLookup)).ToArray());

        Assert.True(client.WaitForClose(seconds: 2));
    }

    [Fact]
    public void ServerTexts_AreCleanedAndCutWhenTheRelayReceivesThem()
    {
        using var relay = new TestRelay(LongLived);
        using RawRelayClient control = relay.Connect();
        RelayRegisterMessage register = RawRelayClient.Register(RawRelayClient.Key(1), "Nombre\nlargo " + new string('n', 1000));
        register.Info.ModVersion = "0.2.0\r\n" + new string('v', 2000);
        string code = control.Request<RelayRegisteredMessage>(register).Code;

        Assert.True(relay.Host.TryGetServer(code, out ServerSession? session));
        Assert.Equal(ProtocolInfo.MaxListedNameLength, session.Info.Name.Length);
        Assert.StartsWith("Nombrelargo", session.Info.Name);
        Assert.Equal(ProtocolInfo.MaxListedVersionLength, session.Info.ModVersion.Length);
        Assert.StartsWith("0.2.0vvv", session.Info.ModVersion);

        using (RawRelayClient player = relay.Connect())
        {
            var lookup = player.Request<RelayLookupResultMessage>(new RelayLookupMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = code });
            Assert.Equal(session.Info.Name, lookup.Name);
            Assert.Equal(session.Info.ModVersion, lookup.ModVersion);
        }

        // El latido (por el control, que admite mensajes grandes) también se limpia.
        control.Send(new RelayHeartbeatMessage
        {
            Info = new RelayServerInfo
            {
                Name = "\tOtro " + new string('o', 20_000),
                IsPublic = true,
                MaxPlayers = 8,
                GameProtocol = ProtocolInfo.Version,
                ModVersion = new string('x', 20_000),
            },
        });
        Assert.True(TestRelay.WaitFor(() => relay.Host.TryGetServer(code, out ServerSession? s) && s.Info.Name.StartsWith("Otro")));
        PublicServerEntry entry = Assert.Single(relay.Host.BuildListResponse().Servers);
        Assert.Equal(ProtocolInfo.MaxListedNameLength, entry.Name.Length);
        Assert.Equal(ProtocolInfo.MaxListedVersionLength, entry.ModVersion.Length);
    }

    // --- Comprobaciones pedidas por el servidor (N4) ---

    [Fact]
    public void RequestedRechecks_WaitForTheMinimumInterval()
    {
        using var relay = new TestRelay(o =>
        {
            LongLived(o);
            o.ProbeTimeout = TimeSpan.FromMilliseconds(300);
            o.MinRecheckInterval = TimeSpan.FromSeconds(2);
        });
        using RawRelayClient control = relay.Connect();
        control.Request<RelayRegisteredMessage>(RawRelayClient.Register(RawRelayClient.Key(1)));
        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount == 1, seconds: 2));
        var clock = Stopwatch.StartNew();

        control.Send(new RelayRecheckMessage());
        control.Send(new RelayRecheckMessage());

        // La pedida no sale hasta 2 s después de terminar la anterior, y la segunda petición (pendiente) tampoco sale
        // enseguida después de esa.
        Assert.False(TestRelay.WaitFor(() => relay.Host.ProbeCount > 1, seconds: 1));
        Assert.True(TestRelay.WaitFor(() => relay.Host.ProbeCount == 2, seconds: 3));
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1));
        Thread.Sleep(400);
        Assert.Equal(2, relay.Host.ProbeCount);
    }

    // --- Bucle de aceptar (T4) ---

    [Fact]
    public async Task AcceptLoop_PausesAfterErrors_AndKeepsAccepting()
    {
        int calls = 0;
        var accepted = new System.Collections.Concurrent.ConcurrentQueue<TcpClient>();
        using var cts = new CancellationTokenSource();
        var throttle = new WarningThrottle(TimeSpan.FromMinutes(1), RateLimiter.Now);
        var clock = Stopwatch.StartNew();

        async ValueTask<TcpClient> Accept(CancellationToken ct)
        {
            int call = Interlocked.Increment(ref calls);
            if (call <= 3)
                throw new SocketException((int)SocketError.TooManyOpenSockets);
            if (call == 4)
                throw new InvalidOperationException("fallo inesperado");
            if (call == 5)
                return new TcpClient();

            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException();
        }

        Task loop = RelayHost.RunAcceptLoopAsync(Accept, accepted.Enqueue, TimeSpan.FromMilliseconds(100), throttle, cts.Token);

        Assert.True(TestRelay.WaitFor(() => accepted.Count == 1, seconds: 3), "El bucle dejó de aceptar tras los errores");
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(350), "No esperó tras cada error");
        Thread.Sleep(200);
        Assert.Equal(6, Volatile.Read(ref calls)); // ni vueltas de más ni se paró

        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(accepted.TryDequeue(out TcpClient? tcp));
        tcp.Dispose();
    }
}
