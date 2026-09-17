using MultiKerbal.Common.Mods;
using MultiKerbal.Server;

namespace MultiKerbal.Tests;

/// <summary>Servidor real en un puerto libre con su propio directorio de datos temporal.</summary>
internal sealed class TestServer : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", Guid.NewGuid().ToString("N"));
    private readonly List<TestClient> _clients = new();

    private readonly string _modPolicy;

    public TestServer(string modPolicy = "warn")
    {
        Log.ConsoleEnabled = false;
        _modPolicy = modPolicy;
        Host = StartHost();
    }

    public ServerHost Host { get; private set; }

    public int Port => Host.Port;

    public TestClient Join(string name) => Track(TestClient.Join(Port, name));

    public TestClient Join(string name, params ModInfo[] mods) => Track(TestClient.Join(Port, name, null, mods));

    public TestClient TryJoin(string name, params ModInfo[] mods) => Track(TestClient.TryJoin(Port, name, null, mods));

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

    private ServerHost StartHost()
    {
        var host = new ServerHost(new ServerConfig
        {
            Port = 0,
            DataDirectory = _dataDirectory,
            ServerName = "Servidor de pruebas",
            ModPolicy = _modPolicy,
        });
        host.Start();
        return host;
    }
}
