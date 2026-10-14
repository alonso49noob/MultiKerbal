using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common.Messages;
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
    private readonly int? _announcedPort;

    /// <param name="relayAddress">Dirección del relay (host:puerto). Sin ella el servidor no usa el relay, para que las pruebas no lleguen a Internet.</param>
    /// <param name="reachableDirectly">
    /// Con false, el servidor anuncia al relay un puerto cerrado: la comprobación de acceso falla y quien entra con
    /// el código pasa por el relay, aunque todo esté en este equipo.
    /// </param>
    public TestServer(string modPolicy = "warn", string gameMode = "sandbox", string? relayAddress = null, bool reachableDirectly = true)
    {
        Log.ConsoleEnabled = false;
        _modPolicy = modPolicy;
        _gameMode = gameMode;
        _relayAddress = relayAddress;
        _announcedPort = reachableDirectly ? null : ReserveFreePort();
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

    /// <summary>Espera a que el servidor se registre en el relay y devuelve su código (sin guion).</summary>
    public string WaitForRelayCode(double seconds = 3)
    {
        RelayLink link = Host.RelayLink ?? throw new InvalidOperationException("El servidor no usa el relay");
        Assert.True(TestRelay.WaitFor(() => link.State == RelayLinkState.Registered, seconds), "El servidor no se registró en el relay");
        return link.Code;
    }

    /// <summary>Un puerto TCP libre ahora mismo (y por tanto cerrado, hasta que alguien lo abra).</summary>
    public static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
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
            RelayAnnouncedPortOverride = _announcedPort,
        }, ShortRelayTimes);
        host.Start();
        return host;
    }
}
