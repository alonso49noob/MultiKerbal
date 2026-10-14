using MultiKerbal.Common.Mods;
using MultiKerbal.Server;
using MultiKerbal.Server.Relay;

namespace MultiKerbal.Tests;

/// <summary>Servidor real en un puerto libre con su propio directorio de datos temporal.</summary>
internal sealed class TestServer : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", Guid.NewGuid().ToString("N"));
    private readonly List<TestClient> _clients = new();

    private readonly string _modPolicy;
    private readonly string _gameMode;
    private readonly string? _relayAddress;

    /// <param name="relayAddress">Dirección del relay (host:puerto). Sin ella el servidor no usa el relay, para que las pruebas no lleguen a Internet.</param>
    public TestServer(string modPolicy = "warn", string gameMode = "sandbox", string? relayAddress = null)
    {
        Log.ConsoleEnabled = false;
        _modPolicy = modPolicy;
        _gameMode = gameMode;
        _relayAddress = relayAddress;
        Host = StartHost();
    }

    public ServerHost Host { get; private set; }

    public int Port => Host.Port;

    public TestClient Join(string name) => Track(TestClient.Join(Port, name));

    public TestClient Join(string name, params ModInfo[] mods) => Track(TestClient.Join(Port, name, null, mods));

    public TestClient TryJoin(string name, params ModInfo[] mods) => Track(TestClient.TryJoin(Port, name, null, mods));

    public TestClient TryJoinWithAccount(string name, string? accountPassword) =>
        Track(TestClient.TryJoin(Port, name, null, null, accountPassword));

    public TestClient Track(TestClient client)
    {
        _clients.Add(client);
        return client;
    }

    public void Restart()
    {
        Host.Stop();
        Host = StartHost();
    }

    public void Dispose()
    {
        foreach (TestClient client in _clients)
            client.Dispose();
        Host.Stop();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Con los tiempos de verdad (latido de 15 s) el relay de pruebas, que caduca a 1 s, daría al servidor por caído.
    private static readonly RelayLinkOptions ShortRelayTimes = new()
    {
        Heartbeat = TimeSpan.FromMilliseconds(250),
        UdpHello = TimeSpan.FromMilliseconds(250),
        RetryInitial = TimeSpan.FromMilliseconds(200),
        RetryMax = TimeSpan.FromSeconds(1),
    };

    private ServerHost StartHost()
    {
        var host = new ServerHost(new ServerConfig
        {
            Port = 0,
            DataDirectory = _dataDirectory,
            ServerName = "Servidor de pruebas",
            ModPolicy = _modPolicy,
            GameMode = _gameMode,
            Relay = _relayAddress != null,
            RelayAddress = _relayAddress ?? string.Empty,
        }, ShortRelayTimes);
        host.Start();
        return host;
    }
}
