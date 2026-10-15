using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Relay;

namespace MultiKerbal.Tests;

/// <summary>
/// Relay real en puertos libres, con carpeta de datos temporal y tiempos cortos.
/// <see cref="Restart"/> conserva la carpeta, las opciones y los puertos.
/// </summary>
internal sealed class TestRelay : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", "relay-" + Guid.NewGuid().ToString("N"));
    private readonly RelayOptions _options;
    private readonly List<RawRelayClient> _clients = new();

    /// <param name="configure">Se aplica después de los tiempos cortos de las pruebas.</param>
    public TestRelay(Action<RelayOptions>? configure = null)
    {
        RelayLog.ConsoleEnabled = false;
        _options = new RelayOptions
        {
            Port = 0,
            ProbePort = 0,
            DataDirectory = _dataDirectory,
            HeartbeatTimeout = TimeSpan.FromSeconds(1),
            FirstMessageTimeout = TimeSpan.FromSeconds(3),
            AttachTimeout = TimeSpan.FromSeconds(3),
            ProbeTimeout = TimeSpan.FromSeconds(1),
            CloseGrace = TimeSpan.FromMilliseconds(300),

            // Todas las pruebas llegan desde 127.0.0.1: los límites por IP se prueban aparte, con valores propios.
            MinRecheckInterval = TimeSpan.Zero,
            MaxNewCodesPerIp = 1000,
            MaxSessionsPerPlayerIp = 1000,
            MaxConnectionsPerIp = 1000,
            NewConnectionsPerSecondPerIp = 10_000,
            NewConnectionBurstPerIp = 10_000,
        };
        configure?.Invoke(_options);
        Host = StartHost();
    }

    public RelayHost Host { get; private set; }

    public string DataDirectory => _dataDirectory;

    public int Port => Host.Port;

    public int ProbePort => Host.ProbePort;

    public string RelayAddress => $"127.0.0.1:{Port}";

    /// <summary>Abre una conexión TCP en bruto contra el relay; se cierra sola al terminar la prueba.</summary>
    public RawRelayClient Connect()
    {
        var client = RawRelayClient.Connect(Port);
        _clients.Add(client);
        return client;
    }

    /// <summary>Para el relay y lo arranca otra vez en los mismos puertos y con la misma carpeta.</summary>
    public void Restart()
    {
        int port = Host.Port;
        int probePort = Host.ProbePort;
        Host.Stop();
        _options.Port = port;
        _options.ProbePort = probePort;

        // El sistema operativo puede tardar un instante en soltar los puertos.
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try
            {
                Host = StartHost();
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        foreach (RawRelayClient client in _clients)
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

    /// <summary>Espera, sondeando, a que se cumpla la condición; devuelve false si pasa el plazo.</summary>
    public static bool WaitFor(Func<bool> condition, double seconds = 4)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    private RelayHost StartHost()
    {
        var host = new RelayHost(_options.Clone());
        try
        {
            host.Start();
        }
        catch
        {
            host.Stop();
            throw;
        }

        return host;
    }
}

/// <summary>Cliente TCP mínimo: manda una trama y lee la respuesta. Sirve mientras el servidor no sabe registrarse.</summary>
internal sealed class RawRelayClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    private RawRelayClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static RawRelayClient Connect(int port)
    {
        var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        tcp.NoDelay = true;
        return new RawRelayClient(tcp);
    }

    /// <param name="gamePort">
    /// Puerto de juego anunciado. Por defecto, uno libre en el que no escucha nadie: el relay lo comprueba, y en el
    /// equipo de pruebas puede haber un servidor de verdad en el puerto normal (6750).
    /// </param>
    public static RelayRegisterMessage Register(byte[] key, string name = "Servidor de pruebas", int? gamePort = null) =>
        new()
        {
            RelayVersion = ProtocolInfo.RelayVersion,
            Key = key,
            GamePort = gamePort ?? TestServer.ReserveFreePort(),
            Info = new RelayServerInfo
            {
                Name = name,
                Players = 0,
                MaxPlayers = 8,
                IsPublic = true,
                GameProtocol = ProtocolInfo.Version,
                ModVersion = "0.0.0-test",
            },
        };

    public static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    public void Send(IMessage message) => SendRaw(FrameCodec.EncodeTcp(message));

    public void SendRaw(byte[] bytes) => _stream.Write(bytes, 0, bytes.Length);

    /// <summary>Lee un mensaje. Devuelve null si el relay cerró la conexión; lanza IOException si no llega nada en el plazo.</summary>
    public IMessage? Receive(double seconds = 4)
    {
        _stream.ReadTimeout = Math.Max(1, (int)(seconds * 1000));
        try
        {
            var header = new byte[FrameCodec.TcpHeaderSize];
            if (!ReadFully(header, header.Length, allowEof: true))
                return null;
            FrameCodec.ParseTcpHeader(header, out int payloadLength, out ushort type);
            var payload = new byte[payloadLength];
            ReadFully(payload, payloadLength, allowEof: false);
            return FrameCodec.DecodePayload(type, payload, 0, payloadLength);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            return null;
        }
    }

    /// <summary>Lee exactamente <paramref name="count"/> bytes tal cual (una sesión ya enganchada pasa bytes, no mensajes).</summary>
    public byte[] ReceiveRaw(int count, double seconds = 4)
    {
        _stream.ReadTimeout = Math.Max(1, (int)(seconds * 1000));
        var buffer = new byte[count];
        ReadFully(buffer, count, allowEof: false);
        return buffer;
    }

    /// <summary>
    /// Lee el siguiente mensaje de un control de servidor, saltándose los resultados de la comprobación de acceso
    /// (<see cref="RelayReachabilityMessage"/>), que el relay manda por su cuenta en cualquier momento.
    /// </summary>
    public T ReceiveControl<T>(double seconds = 4)
        where T : class, IMessage
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            IMessage? message = Receive(Math.Max(0.001, (deadline - DateTime.UtcNow).TotalSeconds));
            if (message is not RelayReachabilityMessage)
                return Assert.IsType<T>(message);
        }
    }

    /// <summary>Manda y espera la respuesta de la misma conexión.</summary>
    public T Request<T>(IMessage message)
        where T : class, IMessage
    {
        Send(message);
        return Assert.IsType<T>(Receive());
    }

    /// <summary>True si el relay cierra la conexión en el plazo (los mensajes que lleguen mientras tanto se descartan).</summary>
    public bool WaitForClose(double seconds = 4)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            double left = (deadline - DateTime.UtcNow).TotalSeconds;
            if (left <= 0)
                return false;

            try
            {
                if (Receive(left) == null)
                    return true;
            }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                return false;
            }
        }
    }

    public void Dispose() => _tcp.Dispose();

    private bool ReadFully(byte[] buffer, int count, bool allowEof)
    {
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(buffer, read, count - read);
            if (n == 0)
            {
                if (read == 0 && allowEof)
                    return false;
                throw new EndOfStreamException();
            }

            read += n;
        }

        return true;
    }
}
