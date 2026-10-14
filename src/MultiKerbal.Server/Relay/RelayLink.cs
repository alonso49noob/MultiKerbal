using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Relay;
using MultiKerbal.Common.Serialization;
using MultiKerbal.Server.Net;

namespace MultiKerbal.Server.Relay;

/// <summary>
/// Enlace del servidor con el relay: abre la conexión de control, se registra, manda latidos y, si algo falla,
/// reintenta solo. Corre en sus propias tareas y nunca toca el bucle principal: los cambios de estado se avisan
/// con <c>changed</c>, que solo encola un evento. <see cref="State"/>, <see cref="Code"/>, <see cref="Error"/> y
/// <see cref="Direct"/> se pueden leer desde cualquier hilo.
/// </summary>
internal sealed class RelayLink
{
    /// <summary>Plazo para conectar y para que el relay conteste al registro.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private readonly string _relayAddress;
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _key;
    private readonly int _gamePort;
    private readonly Func<RelayServerInfo> _info;
    private readonly NetworkServer _network;
    private readonly Action _changed;
    private readonly RelayLinkOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private Task? _task;
    private RelayLinkState _state = RelayLinkState.Off;
    private string _code = string.Empty;
    private string _error = string.Empty;
    private bool _direct;

    /// <summary>La conexión actual llegó a registrarse. Solo la toca la tarea del enlace.</summary>
    private bool _registeredThisConnection;

    /// <param name="relayAddress">"host" o "host:puerto" (por defecto, el puerto del relay).</param>
    /// <param name="info">Datos al día del servidor; se pide en cada latido, desde el hilo del enlace.</param>
    /// <param name="network">Red del juego. La tarea 7 la usa para el UDP; hasta entonces no se toca.</param>
    /// <param name="changed">Se llama en cada cambio de estado, desde el hilo del enlace. Debe ser inmediata (encolar).</param>
    public RelayLink(string relayAddress, byte[] key, int gamePort, Func<RelayServerInfo> info, NetworkServer network, Action changed, RelayLinkOptions options)
    {
        if (!RelayEndpoint.TryParse(relayAddress, ProtocolInfo.DefaultRelayPort, out string host, out int port))
            throw new ArgumentException($"Dirección del relay inválida: {relayAddress}", nameof(relayAddress));

        _relayAddress = relayAddress.Trim();
        _host = host;
        _port = port;
        _key = key;
        _gamePort = gamePort;
        _info = info;
        _network = network;
        _changed = changed;
        _options = options;
    }

    public RelayLinkState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    /// <summary>Código sin guion (se muestra con <see cref="ServerCode.Format"/>). Vacío si no está registrado.</summary>
    public string Code
    {
        get
        {
            lock (_sync)
                return _code;
        }
    }

    /// <summary>Motivo del rechazo, o del último fallo mientras reintenta. Vacío si todo va bien.</summary>
    public string Error
    {
        get
        {
            lock (_sync)
                return _error;
        }
    }

    /// <summary>Los puertos son alcanzables desde fuera (lo fija la comprobación de la tarea 7).</summary>
    public bool Direct
    {
        get
        {
            lock (_sync)
                return _direct;
        }
    }

    /// <summary>Dirección del relay tal como se escribió en la configuración.</summary>
    public string RelayAddress => _relayAddress;

    public void Start()
    {
        if (_task != null)
            return;

        SetState(RelayLinkState.Connecting, string.Empty, string.Empty);
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Cierra el control y deja de reintentar. Vuelve en cuanto la tarea acaba (2 s como mucho).</summary>
    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _task?.Wait(StopWait);
        }
        catch (AggregateException)
        {
        }

        // En silencio: quien para el enlace ya sabe que está apagado.
        lock (_sync)
        {
            _state = RelayLinkState.Off;
            _code = string.Empty;
            _direct = false;
        }
    }

    /// <summary>Repetir la comprobación de acceso directo. Lo implementa la tarea 7.</summary>
    public void Recheck()
    {
    }

    /// <summary>Un jugador entra por el relay y el relay le ha dado esta ficha UDP. Lo implementa la tarea 7.</summary>
    public void OnUdpToken(ClientConnection connection)
    {
    }

    private async Task RunAsync(CancellationToken ct)
    {
        TimeSpan delay = _options.RetryInitial;
        while (!ct.IsCancellationRequested)
        {
            string failure;
            _registeredThisConnection = false;
            try
            {
                await RunConnectionAsync(ct);
                if (State == RelayLinkState.Rejected)
                    return;

                failure = Lang.T("Se perdió la conexión con el relay", "The connection to the relay was lost");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                failure = Lang.T($"No se pudo hablar con el relay: {ex.Message}", $"Could not talk to the relay: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Un fallo del programa no debe matar el enlace para siempre: se anota y se reintenta.
                Log.Error($"Error inesperado en el enlace con el relay: {ex}");
                failure = Lang.T($"Error interno: {ex.Message}", $"Internal error: {ex.Message}");
            }

            if (ct.IsCancellationRequested)
                return;

            // Un servidor que estuvo registrado y se cae vuelve a intentarlo pronto; los fallos seguidos espacian los intentos.
            if (_registeredThisConnection)
                delay = _options.RetryInitial;

            SetState(RelayLinkState.Connecting, string.Empty, failure);
            Log.Warn($"Relay {_relayAddress}: {failure}. Reintento en {delay.TotalSeconds:0.#} s");

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RetryMax.Ticks));
        }
    }

    /// <summary>
    /// Una conexión completa: conectar, registrarse y atender el control hasta que se corte. Anota si llegó a
    /// registrarse (<see cref="_registeredThisConnection"/>). Si el relay rechaza el registro, deja el estado en
    /// Rejected y vuelve.
    /// </summary>
    private async Task RunConnectionAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(_host, _port, connect.Token);
        }

        tcp.NoDelay = true;
        NetworkStream stream = tcp.GetStream();
        var writeLock = new SemaphoreSlim(1, 1);

        // Registro: el relay contesta con el código o con el motivo del rechazo.
        IMessage? reply;
        using (var register = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            register.CancelAfter(ConnectTimeout);
            await WriteAsync(stream, writeLock, new RelayRegisterMessage
            {
                RelayVersion = ProtocolInfo.RelayVersion,
                Key = _key,
                GamePort = _gamePort,
                Info = _info(),
            }, register.Token);
            reply = await ReadAsync(stream, register.Token);
        }

        switch (reply)
        {
            case RelayRegisteredMessage registered:
                if (!ServerCode.TryNormalize(registered.Code ?? string.Empty, out string code))
                    throw new ProtocolException("El relay mandó un código inválido");

                _registeredThisConnection = true;
                SetState(RelayLinkState.Registered, code, string.Empty);
                break;

            case RelayRejectedMessage rejected:
                string reason = string.IsNullOrWhiteSpace(rejected.Reason)
                    ? Lang.T("El relay rechazó el registro", "The relay rejected the registration")
                    : rejected.Reason;
                SetState(RelayLinkState.Rejected, string.Empty, reason);
                Log.Warn($"El relay {_relayAddress} ha rechazado el registro: {reason}");
                return;

            case null:
                throw new IOException("El relay cerró la conexión al registrarse");

            default:
                throw new ProtocolException($"Respuesta inesperada del relay al registrarse: {reply.Type}");
        }

        // Registrado: los latidos salen por su cuenta y esta tarea lee el control. Si falla cualquiera de las dos,
        // se cancela la otra y se cierra la conexión.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task heartbeats = HeartbeatLoopAsync(stream, writeLock, session);
        try
        {
            await ReadLoopAsync(stream, session.Token);
        }
        finally
        {
            session.Cancel();
            await heartbeats;
        }
    }

    private async Task HeartbeatLoopAsync(NetworkStream stream, SemaphoreSlim writeLock, CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                await Task.Delay(_options.Heartbeat, session.Token);
                await WriteAsync(stream, writeLock, new RelayHeartbeatMessage { Info = _info() }, session.Token);
            }
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Si el latido no sale, la conexión está rota: se corta la lectura para reconectar ya.
            session.Cancel();
        }
    }

    /// <summary>Lee el control hasta que se corte. Aquí se añaden los mensajes que el relay manda al servidor.</summary>
    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (true)
        {
            IMessage? message = await ReadAsync(stream, ct);
            if (message == null)
                return;

            switch (message)
            {
                // La tarea 6 añade RelayIncoming (un jugador quiere entrar por el relay) y la tarea 7
                // RelayReachability y RelayUdpToken.
                default:
                    Log.Warn($"El relay mandó un mensaje inesperado por el control: {message.Type}");
                    break;
            }
        }
    }

    private void SetState(RelayLinkState state, string code, string error)
    {
        lock (_sync)
        {
            _state = state;
            _code = code;
            _error = error;
            if (state != RelayLinkState.Registered)
                _direct = false;
        }

        _changed();
    }

    /// <summary>Lee un mensaje del relay, respetando su límite de tamaño. Null si el relay cierra la conexión.</summary>
    private static async Task<IMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[FrameCodec.TcpHeaderSize];
        try
        {
            await stream.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        FrameCodec.ParseTcpHeader(header, out int payloadLength, out ushort type);
        if (payloadLength > ProtocolInfo.MaxRelayMessageBytes)
            throw new ProtocolException($"Mensaje demasiado grande del relay: {payloadLength} bytes");

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, ct);
        return FrameCodec.DecodePayload(type, payload, 0, payloadLength);
    }

    /// <summary>Escribe un mensaje. El cerrojo evita que dos mensajes se mezclen (latido y los que añadan otras tareas).</summary>
    private static async Task WriteAsync(Stream stream, SemaphoreSlim writeLock, IMessage message, CancellationToken ct)
    {
        byte[] frame = FrameCodec.EncodeTcp(message);
        await writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(frame, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static bool IsConnectionError(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or ProtocolException or OperationCanceledException;
}
