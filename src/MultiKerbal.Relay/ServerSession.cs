using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Relay;

/// <summary>
/// Un servidor registrado: su conexión de control (permanente), los datos del último latido y el secreto UDP.
/// Si no llega nada por el control en <see cref="RelayOptions.HeartbeatTimeout"/>, se cierra.
/// </summary>
internal sealed class ServerSession
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private long _lastSeenTicks;
    private int _isClosed;
    private volatile RelayServerInfo _info;

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

    public DateTime LastSeen => new(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);

    public bool IsClosed => Volatile.Read(ref _isClosed) != 0;

    /// <summary>Manda un mensaje por el control. Seguro entre hilos.</summary>
    public async Task SendAsync(IMessage message, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await _writeLock.WaitAsync(linked.Token);
        try
        {
            await RelayWire.WriteAsync(_stream, message, linked.Token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Lee el control hasta que se cierre, se corte o caduque. Cada mensaje cuenta como señal de vida,
    /// y el plazo corre desde el principio de cada mensaje (un mensaje a medias no lo alarga).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
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
                        // La tarea 7 repite aquí la comprobación de acceso directo.
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
}
