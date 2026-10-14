using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Mods;
using MultiKerbal.Common.Net;

namespace MultiKerbal.Tests;

/// <summary>Cliente sin KSP que usa el mismo NetClient que el mod, para pruebas de integración.</summary>
internal sealed class TestClient : IDisposable
{
    private readonly List<IMessage> _messages = new();
    private readonly List<NetEvent> _events = new();

    public NetClient Net { get; } = new();

    public HandshakeResponseMessage? Welcome { get; private set; }

    public static TestClient Connect(int port)
    {
        var client = new TestClient();
        client.Net.Connect("127.0.0.1", port);
        client.WaitForEvent(NetEventType.Connected);
        return client;
    }

    /// <summary>Contraseña de cuenta con la que entran los clientes de prueba si no se indica otra.</summary>
    public const string DefaultAccountPassword = "secreto";

    public static TestClient Join(int port, string name, string? password = null, ModInfo[]? mods = null, string accountPassword = DefaultAccountPassword)
    {
        TestClient client = TryJoin(port, name, password, mods, accountPassword);
        Assert.True(client.Welcome!.Accepted, client.Welcome.RejectReason);
        return client;
    }

    /// <summary>Como <see cref="Join"/> pero sin exigir que el servidor acepte (para probar rechazos).</summary>
    public static TestClient TryJoin(int port, string name, string? password = null, ModInfo[]? mods = null, string? accountPassword = DefaultAccountPassword)
    {
        TestClient client = Connect(port);
        client.Handshake(name, password, mods, accountPassword);
        return client;
    }

    /// <summary>Empieza a conectar con un código (consulta al relay y, si hace falta, entra por él) sin esperar a nada.</summary>
    public static TestClient StartWithCode(string relayAddress, string code)
    {
        var client = new TestClient();
        client.Net.Connect(ConnectPlan.ForCode(relayAddress, code));
        return client;
    }

    /// <summary>Entra con un código, como el mod, y exige que el servidor lo acepte.</summary>
    public static TestClient JoinWithCode(string relayAddress, string code, string name)
    {
        TestClient client = StartWithCode(relayAddress, code);
        WaitUntil(() => client.Net.State != NetClientState.Connecting);
        if (client.Net.State != NetClientState.Connected)
            Assert.Fail($"No se pudo entrar con el código: {client.WaitForEvent(NetEventType.Disconnected).Reason}");

        client.WaitForEvent(NetEventType.Connected);
        client.Handshake(name, null, null, DefaultAccountPassword);
        Assert.True(client.Welcome!.Accepted, client.Welcome.RejectReason);
        return client;
    }

    public void Send(IMessage message, Delivery delivery = Delivery.Reliable) => Net.Send(message, delivery);

    private void Handshake(string name, string? password, ModInfo[]? mods, string? accountPassword)
    {
        Send(new HandshakeRequestMessage
        {
            ProtocolVersion = ProtocolInfo.Version,
            PlayerName = name,
            Password = password,
            AccountPassword = accountPassword,
            ModVersion = "test",
            GameVersion = "test",
            Mods = mods ?? [],
        });

        Welcome = WaitFor<HandshakeResponseMessage>();
    }

    public T WaitFor<T>(Func<T, bool>? predicate = null, int timeoutMs = 5000)
        where T : class, IMessage
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Pump();
            for (int i = 0; i < _messages.Count; i++)
            {
                if (_messages[i] is T typed && (predicate == null || predicate(typed)))
                {
                    _messages.RemoveAt(i);
                    return typed;
                }
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No llegó {typeof(T).Name}. Recibidos: {string.Join(", ", _messages.Select(m => m.Type))}");
            Thread.Sleep(5);
        }
    }

    /// <summary>Mensajes de tipo T ya recibidos y aún no consumidos (sin esperar).</summary>
    public IReadOnlyList<T> Received<T>()
        where T : class, IMessage
    {
        Pump();
        return _messages.OfType<T>().ToList();
    }

    public NetEvent WaitForEvent(NetEventType type, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Pump();
            NetEvent? match = _events.FirstOrDefault(e => e.Type == type);
            if (match != null)
            {
                _events.Remove(match);
                return match;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No llegó el evento {type}");
            Thread.Sleep(5);
        }
    }

    public static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("La condición no se cumplió a tiempo");
            Thread.Sleep(5);
        }
    }

    public void Dispose() => Net.Dispose();

    private void Pump()
    {
        while (Net.TryDequeue(out NetEvent netEvent))
        {
            if (netEvent.Type == NetEventType.Message)
                _messages.Add(netEvent.Message);
            else
                _events.Add(netEvent);
        }
    }
}
