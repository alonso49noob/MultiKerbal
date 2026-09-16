using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
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

    public static TestClient Join(int port, string name, string? password = null)
    {
        TestClient client = Connect(port);
        client.Send(new HandshakeRequestMessage
        {
            ProtocolVersion = ProtocolInfo.Version,
            PlayerName = name,
            Password = password,
            ModVersion = "test",
            GameVersion = "test",
        });

        HandshakeResponseMessage response = client.WaitFor<HandshakeResponseMessage>();
        Assert.True(response.Accepted, response.RejectReason);
        client.Welcome = response;
        return client;
    }

    public void Send(IMessage message, Delivery delivery = Delivery.Reliable) => Net.Send(message, delivery);

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
