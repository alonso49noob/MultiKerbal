using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Relay;
using MultiKerbal.Common.Serialization;
using MultiKerbal.Server.Net;

namespace MultiKerbal.Server.Relay;

/// <summary>
/// Enlace del servidor con el relay: abre la conexión de control, se registra, manda latidos y el hola UDP, avisa
/// de las fichas UDP de los jugadores que entran por el relay y, si algo falla, reintenta solo. Corre en sus propias tareas y nunca toca el bucle principal: los cambios de estado se avisan
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

    /// <summary>Peticiones de mandar un latido ya (como mucho una pendiente).</summary>
    private readonly SemaphoreSlim _beatNow = new(0, 1);

    /// <summary>Control registrado ahora mismo, para los mensajes que salen fuera de su tarea; null si no hay. Con <see cref="_sync"/>.</summary>
    private Control? _control;

    /// <summary>La conexión actual llegó a registrarse. Solo la toca la tarea del enlace.</summary>
    private bool _registeredThisConnection;

    /// <param name="relayAddress">"host" o "host:puerto" (por defecto, el puerto del relay).</param>
    /// <param name="info">Datos al día del servidor; se pide en cada latido, desde el hilo del enlace.</param>
    /// <param name="network">Red del juego: adopta a los jugadores que entran por el relay y manda el hola UDP desde su socket.</param>
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

    /// <summary>Estado, código y acceso directo leídos a la vez, para que no se mezclen valores de momentos distintos.</summary>
    public (RelayLinkState State, string Code, string Error, bool Direct) Snapshot()
    {
        lock (_sync)
            return (_state, _code, _error, _direct);
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

    /// <summary>Se puede entrar sin pasar por el relay: su comprobación llegó al puerto de juego por TCP y por UDP.</summary>
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

    /// <summary>
    /// Manda un latido sin esperar al siguiente (por ejemplo, porque cambió el estado público). No bloquea; si no hay
    /// control registrado, el próximo latido ya llevará los datos nuevos. Varias peticiones seguidas hacen un solo latido.
    /// </summary>
    public void HeartbeatNow()
    {
        try
        {
            if (_beatNow.CurrentCount == 0)
                _beatNow.Release();
        }
        catch (SemaphoreFullException)
        {
            // Otra petición llegó a la vez: ya hay una pendiente.
        }
    }

    /// <summary>Pide al relay que repita la comprobación de acceso directo. No bloquea; sin registro no hace nada.</summary>
    public void Recheck() => SendOnControl(new RelayRecheckMessage());

    /// <summary>
    /// Un jugador que entró por el relay ya tiene ficha UDP: se le dice al relay, para que reenvíe su UDP.
    /// No bloquea. Si ahora no hay control, el jugador sigue con todo por TCP.
    /// </summary>
    public void OnUdpToken(ClientConnection connection)
    {
        if (connection.RelaySessionId == 0 || connection.UdpToken == 0)
            return;

        SendOnControl(new RelayUdpTokenMessage { SessionId = connection.RelaySessionId, Token = connection.UdpToken });
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

        ulong udpSecret;
        string code;
        switch (reply)
        {
            case RelayRegisteredMessage registered:
                if (!ServerCode.TryNormalize(registered.Code ?? string.Empty, out code))
                    throw new ProtocolException("El relay mandó un código inválido");

                udpSecret = registered.UdpSecret;
                _registeredThisConnection = true;
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

        // Registrado: los latidos y el hola UDP salen por su cuenta y esta tarea lee el control. Si falla cualquiera
        // de las tres, se cancela todo y se cierra la conexión. El control queda a mano antes de anunciar el registro,
        // para que un Recheck u OnUdpToken justo después no se pierdan.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var control = new Control(stream, writeLock, session);
        lock (_sync)
            _control = control;
        SetState(RelayLinkState.Registered, code, string.Empty);

        Task heartbeats = HeartbeatLoopAsync(stream, writeLock, session);
        Task hellos = UdpHelloLoopAsync(RelayUdpEndPoint(tcp), udpSecret, session);
        try
        {
            await ReadLoopAsync(stream, session.Token);
        }
        finally
        {
            lock (_sync)
            {
                if (_control == control)
                    _control = null;
            }

            session.Cancel();
            await heartbeats;
            await hellos;
        }
    }

    /// <summary>El puerto UDP del relay: la misma dirección a la que se conectó el control, en el mismo puerto.</summary>
    private IPEndPoint RelayUdpEndPoint(TcpClient tcp)
    {
        IPAddress address = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return new IPEndPoint(address, _port);
    }

    /// <summary>
    /// Manda el hola UDP desde el socket de juego nada más registrarse y luego cada <see cref="RelayLinkOptions.UdpHello"/>:
    /// así el relay sabe por qué dirección le llega el UDP del servidor, y el router de casa deja pasar las respuestas.
    /// </summary>
    private async Task UdpHelloLoopAsync(IPEndPoint relay, ulong secret, CancellationTokenSource session)
    {
        try
        {
            byte[] hello = FrameCodec.EncodeUdp(new RelayUdpHelloMessage { UdpSecret = secret }, 0);
            while (!session.IsCancellationRequested)
            {
                _network.SendRawUdp(hello, relay);
                await Task.Delay(_options.UdpHello, session.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Como con el latido: sin hola el UDP de los jugadores por el relay no funciona, así que se reconecta.
            Log.Error($"Error inesperado al mandar el hola UDP al relay: {ex}");
            session.Cancel();
        }
    }

    /// <summary>
    /// Manda un mensaje por el control actual en otra tarea, sin hacer esperar a quien llama (el bucle principal).
    /// Si no sale, la conexión está rota: se corta para reconectar.
    /// </summary>
    private void SendOnControl(IMessage message)
    {
        Control? control;
        lock (_sync)
            control = _control;
        if (control == null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(control.Stream, control.WriteLock, message, control.Session.Token);
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                try
                {
                    control.Session.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Esa conexión ya terminó.
                }
            }
        });
    }

    private async Task HeartbeatLoopAsync(NetworkStream stream, SemaphoreSlim writeLock, CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                // Cada intervalo, o antes si piden un latido ya (HeartbeatNow).
                await _beatNow.WaitAsync(_options.Heartbeat, session.Token);
                await WriteAsync(stream, writeLock, new RelayHeartbeatMessage { Info = _info() }, session.Token);
            }
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Si el latido no sale, la conexión está rota: se corta la lectura para reconectar ya.
            session.Cancel();
        }
        catch (Exception ex)
        {
            // Un fallo del programa (al pedir los datos o al codificar): sin latidos el relay nos daría por caídos,
            // así que no se deja la lectura esperando; se anota y se reconecta.
            Log.Error($"Error inesperado al mandar el latido al relay: {ex}");
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
                case RelayIncomingMessage incoming:
                    // Aparte, para no parar la lectura del control mientras se conecta.
                    _ = AttachAsync(incoming, _cts.Token);
                    break;

                case RelayReachabilityMessage reachability:
                    lock (_sync)
                        _direct = _state == RelayLinkState.Registered && reachability.Tcp && reachability.Udp;
                    _changed();
                    break;

                default:
                    Log.Warn($"El relay mandó un mensaje inesperado por el control: {message.Type}");
                    break;
            }
        }
    }

    /// <summary>
    /// Un jugador entra por el relay: se abre una conexión nueva con el relay, se engancha a su sesión y, desde ahí,
    /// es una conexión de juego más. Si falla, el relay acaba dando un error al jugador; aquí solo se avisa.
    /// </summary>
    private async Task AttachAsync(RelayIncomingMessage incoming, CancellationToken ct)
    {
        string player = incoming.ClientAddress ?? "?";
        var tcp = new TcpClient();
        bool adopted = false;
        try
        {
            using (var attach = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attach.CancelAfter(ConnectTimeout);
                await tcp.ConnectAsync(_host, _port, attach.Token);
                byte[] frame = FrameCodec.EncodeTcp(new RelayAttachMessage
                {
                    RelayVersion = ProtocolInfo.RelayVersion,
                    SessionId = incoming.SessionId,
                    Secret = incoming.Secret,
                });
                await tcp.GetStream().WriteAsync(frame, attach.Token);
            }

            _network.Adopt(tcp, player, incoming.SessionId);
            adopted = true;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            if (!ct.IsCancellationRequested)
                Log.Warn($"No se pudo enganchar por el relay al jugador {player}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"Error inesperado al enganchar por el relay al jugador {player}: {ex}");
        }
        finally
        {
            if (!adopted)
                tcp.Dispose();
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

    /// <summary>Un control registrado: su flujo, el cerrojo de escritura y la cancelación de esa conexión.</summary>
    private sealed record Control(NetworkStream Stream, SemaphoreSlim WriteLock, CancellationTokenSource Session);
}
