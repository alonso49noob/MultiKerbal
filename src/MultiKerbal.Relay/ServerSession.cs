using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Relay;

/// <summary>
/// Un servidor registrado: su conexión de control (permanente), los datos del último latido, el secreto UDP con su
/// dirección UDP y el resultado de la comprobación de acceso directo. Si no llega nada por el control en
/// <see cref="RelayOptions.HeartbeatTimeout"/>, se cierra.
/// </summary>
internal sealed class ServerSession
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closed = new();

    /// <summary>Peticiones de repetir la comprobación (como mucho una pendiente).</summary>
    private readonly SemaphoreSlim _recheck = new(0, 1);
    private long _lastSeenTicks;
    private int _isClosed;
    private volatile RelayServerInfo _info;
    private volatile EndPoint? _udpEndPoint;

    /// <summary>Resultado de la última comprobación, los dos a la vez (nunca uno nuevo con otro viejo).</summary>
    private volatile Reachability _reachability = new(false, false);

    public ServerSession(TcpClient tcp, string code, ulong udpSecret, RelayServerInfo info, int gamePort, IPAddress remoteAddress, TimeSpan heartbeatTimeout)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _heartbeatTimeout = heartbeatTimeout;
        Code = code;
        UdpSecret = udpSecret;
        _info = info;
        GamePort = gamePort;
        RemoteAddress = remoteAddress;
        RegisteredAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>Código sin guion.</summary>
    public string Code { get; }

    public ulong UdpSecret { get; }

    /// <summary>Datos del último latido.</summary>
    public RelayServerInfo Info => _info;

    /// <summary>Puerto del juego en el servidor.</summary>
    public int GamePort { get; }

    /// <summary>IP desde la que llegó el control (IPv4 normal aunque el socket sea de doble pila).</summary>
    public IPAddress RemoteAddress { get; }

    public DateTime RegisteredAt { get; }

    /// <summary>Dirección desde la que llega el hola UDP del servidor (su socket de juego); null hasta el primero.</summary>
    public EndPoint? UdpEndPoint
    {
        get => _udpEndPoint;
        set => _udpEndPoint = value;
    }

    /// <summary>La última comprobación pudo abrir una TCP al puerto de juego.</summary>
    public bool DirectTcp => _reachability.Tcp;

    /// <summary>La última comprobación recibió respuesta por UDP desde el socket de comprobaciones.</summary>
    public bool DirectUdp => _reachability.Udp;

    /// <summary>Se puede entrar sin pasar por el relay: funcionan la TCP y el UDP (de la misma comprobación).</summary>
    public bool IsDirect
    {
        get
        {
            Reachability reachability = _reachability;
            return reachability.Tcp && reachability.Udp;
        }
    }

    /// <summary>Apunta el resultado de una comprobación. Devuelve true si ha cambiado.</summary>
    public bool SetReachability(bool tcp, bool udp)
    {
        var next = new Reachability(tcp, udp);
        return Interlocked.Exchange(ref _reachability, next) != next;
    }

    /// <summary>Se cancela al cerrar la sesión.</summary>
    public CancellationToken ClosedToken => _closed.Token;

    public DateTime LastSeen => new(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);

    public bool IsClosed => Volatile.Read(ref _isClosed) != 0;

    /// <summary>
    /// Manda un mensaje por el control. Seguro entre hilos. <paramref name="ct"/> solo corta la espera del turno
    /// de escritura: una trama empezada no se corta nunca a medias, porque estropearía el control. Si la escritura
    /// no termina en el plazo de caducidad, o falla, se cierra la sesión.
    /// </summary>
    public async Task SendAsync(IMessage message, CancellationToken ct)
    {
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token))
            await _writeLock.WaitAsync(wait.Token);

        try
        {
            using var write = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            write.CancelAfter(_heartbeatTimeout);
            await RelayWire.WriteAsync(_stream, message, write.Token);
        }
        catch (Exception ex) when (RelayWire.IsConnectionError(ex))
        {
            Close();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Pide repetir la comprobación de acceso directo. Si ya hay una pedida, no se acumula otra.</summary>
    public void RequestRecheck()
    {
        try
        {
            if (_recheck.CurrentCount == 0)
                _recheck.Release();
        }
        catch (SemaphoreFullException)
        {
            // Otra petición llegó a la vez: ya hay una pendiente.
        }
    }

    /// <summary>Espera hasta que se pida otra comprobación o pase <paramref name="interval"/>.</summary>
    public async Task WaitForRecheckAsync(TimeSpan interval, CancellationToken ct) => await _recheck.WaitAsync(interval, ct);

    /// <summary>
    /// Lee el control hasta que se cierre, se corte o caduque. Cada mensaje cuenta como señal de vida,
    /// y el plazo corre desde el principio de cada mensaje (un mensaje a medias no lo alarga).
    /// </summary>
    /// <param name="onUdpToken">Se llama con cada <see cref="RelayUdpTokenMessage"/> del servidor, desde este hilo.</param>
    public async Task RunAsync(Action<ServerSession, RelayUdpTokenMessage> onUdpToken, CancellationToken ct)
    {
        try
        {
            while (!IsClosed)
            {
                using var frame = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
                frame.CancelAfter(_heartbeatTimeout);

                IMessage? message = await RelayWire.ReadAsync(_stream, frame.Token);
                if (message == null)
                    break;

                Touch();
                switch (message)
                {
                    case RelayHeartbeatMessage { Info: not null } heartbeat:
                        _info = heartbeat.Info;
                        break;

                    case RelayRecheckMessage:
                        RequestRecheck();
                        break;

                    case RelayUdpTokenMessage token:
                        onUdpToken(this, token);
                        break;

                    default:
                        RelayLog.Warn($"[{Code}] Mensaje inesperado por el control: {message.Type}");
                        return;
                }
            }
        }
        catch (Exception ex) when (RelayWire.IsConnectionError(ex))
        {
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Cierra la conexión de control. Se puede llamar varias veces desde cualquier hilo.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0)
            return;

        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _tcp.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);

    private sealed record Reachability(bool Tcp, bool Udp);
}
