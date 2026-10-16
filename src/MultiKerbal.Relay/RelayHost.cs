using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;

namespace MultiKerbal.Relay;

/// <summary>
/// El relay: acepta conexiones TCP, mira cuál es su primer mensaje y las encamina. Esta parte lleva el registro
/// de servidores (códigos permanentes, latidos, caducidad), la consulta por código y los sockets UDP (reenvío en
/// <see cref="Port"/> y comprobaciones de acceso en <see cref="ProbePort"/>).
/// </summary>
public sealed partial class RelayHost
{
    private const int SioUdpConnReset = -1744830452;

    private readonly RelayOptions _options;
    private readonly object _registryLock = new();

    /// <summary>Servidores vivos por código (sin guion). Se toca siempre con <see cref="_registryLock"/>.</summary>
    private readonly Dictionary<string, ServerSession> _servers = new();

    /// <summary>Todas las conexiones abiertas (también las que aún no han dicho qué son), para cerrarlas al parar.</summary>
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly WarningThrottle _warnings;
    private readonly ConnectionGate _gate;
    private readonly NewCodeBudget _newCodes;
    private readonly SessionBroker _broker;
    private readonly UdpForwarder _forwarder;
    private readonly ReachabilityProber _prober;
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private CodeStore? _codes;
    private TcpListener? _listener;
    private Socket? _udpSocket;
    private Socket? _probeSocket;
    private bool _stopping;
    private int _stopped;

    internal RelayHost(RelayOptions options)
    {
        _options = options;
        _warnings = new WarningThrottle(options.WarningInterval, RateLimiter.Now);
        _gate = new ConnectionGate(options, RateLimiter.Now);
        _newCodes = new NewCodeBudget(options.MaxNewCodesPerIp, options.NewCodeWindow, RateLimiter.Now);
        _forwarder = new UdpForwarder(FindServerBySecret);
        _broker = new SessionBroker(options, FindServer, _forwarder, _warnings);
        _prober = new ReachabilityProber(options);
    }

    /// <summary>Puerto TCP y UDP real (si se pidió el 0, el que eligió el sistema).</summary>
    public int Port { get; private set; }

    /// <summary>Puerto UDP real desde el que saldrán las comprobaciones de acceso directo.</summary>
    public int ProbePort { get; private set; }

    internal int LiveServerCount
    {
        get
        {
            lock (_registryLock)
                return _servers.Count;
        }
    }

    /// <summary>Sesiones de jugadores por el relay abiertas ahora (esperando el enganche o pasando bytes).</summary>
    internal int RelaySessionCount => _broker.Count;

    /// <summary>Comprobaciones de acceso directo terminadas (para los tests).</summary>
    internal int ProbeCount => _prober.ProbeCount;

    /// <summary>Texto de una versión distinta, en el idioma del relay: solo lo usa un mod que no conozca el código del error.</summary>
    internal static string VersionMismatchText(int theirs) => Lang.T(
        $"El relay usa otra versión del protocolo (relay {ProtocolInfo.RelayVersion}, tú {theirs}). Actualiza MultiKerbal.",
        $"The relay uses another protocol version (relay {ProtocolInfo.RelayVersion}, you {theirs}). Update MultiKerbal.");

    /// <summary>Busca un servidor vivo por su código (con o sin guion, en mayúsculas o minúsculas).</summary>
    internal bool TryGetServer(string code, [NotNullWhen(true)] out ServerSession? session)
    {
        session = null;
        if (!ServerCode.TryNormalize(code, out string normalized))
            return false;

        lock (_registryLock)
            return _servers.TryGetValue(normalized, out session);
    }

    private ServerSession? FindServer(string code) => TryGetServer(code, out ServerSession? server) ? server : null;

    /// <summary>Servidor vivo con ese secreto UDP (el de su hola); null si no hay ninguno.</summary>
    private ServerSession? FindServerBySecret(ulong secret)
    {
        lock (_registryLock)
        {
            foreach (ServerSession server in _servers.Values)
            {
                if (server.UdpSecret == secret)
                    return server;
            }
        }

        return null;
    }

    /// <summary>
    /// Arranca el relay. Lanza <see cref="InvalidDataException"/> si codes.json está dañado: no se arranca con
    /// la lista vacía porque se reasignarían códigos que los jugadores ya conocen.
    /// </summary>
    public void Start()
    {
        _codes = new CodeStore(_options.DataDirectory);
        try
        {
            _listener = CreateListener(_options.Port);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _udpSocket = CreateUdpSocket(Port);
            _probeSocket = CreateUdpSocket(_options.ProbePort);
            ProbePort = ((IPEndPoint)_probeSocket.LocalEndPoint!).Port;
        }
        catch
        {
            Stop();
            throw;
        }

        _forwarder.Start(_udpSocket, _cts.Token);
        _prober.Start(_probeSocket, _cts.Token);
        _ = AcceptLoopAsync(_cts.Token);
        RelayLog.Info($"Relay escuchando en el puerto {Port} (TCP y UDP); comprobaciones desde el UDP {ProbePort}");
        RelayLog.Info($"{_codes.Count} código(s) guardado(s) en {Path.GetFullPath(_options.DataDirectory)}");
    }

    /// <summary>Para el relay: cierra el puerto y todas las conexiones. Se puede llamar varias veces.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        ServerSession[] sessions;
        lock (_registryLock)
        {
            _stopping = true;
            sessions = _servers.Values.ToArray();
            _servers.Clear();
        }

        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        _udpSocket?.Dispose();
        _probeSocket?.Dispose();
        foreach (ServerSession session in sessions)
            session.Close();
        foreach (TcpClient tcp in _connections.Keys)
            Dispose(tcp);

        _stopRequested.Set();
    }

    /// <summary>Pide parar (Ctrl+C o el comando <c>stop</c>). Quien espera en <see cref="WaitForStopRequest"/> se despierta.</summary>
    public void RequestStop() => _stopRequested.Set();

    public void WaitForStopRequest() => _stopRequested.Wait();

    /// <summary>
    /// El relay solo escucha por IPv4 (IPv6 queda fuera del diseño). Así, además, los límites por IP no se pueden saltar
    /// cambiando de dirección dentro de un bloque IPv6 propio (una /64 tiene trillones).
    /// </summary>
    private static TcpListener CreateListener(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        return listener;
    }

    /// <summary>Socket UDP solo IPv4, como el puerto TCP (ver <see cref="CreateListener"/>).</summary>
    private static Socket CreateUdpSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        // En Windows, un aviso ICMP de "puerto inalcanzable" (un jugador que se fue, una sonda a un puerto cerrado)
        // haría fallar la siguiente lectura del socket.
        if (OperatingSystem.IsWindows())
            socket.IOControl(SioUdpConnReset, new byte[4], null);
        return socket;
    }

    private Task AcceptLoopAsync(CancellationToken ct) =>
        RunAcceptLoopAsync(token => _listener!.AcceptTcpClientAsync(token), tcp => OnAccepted(tcp, ct), _options.AcceptErrorDelay, _warnings, ct);

    /// <summary>
    /// Acepta conexiones hasta que se pare el relay (cancelación o puerto cerrado). Un error al aceptar (por ejemplo, sin
    /// descriptores libres) se anota y se espera <paramref name="errorDelay"/> antes de seguir, para no dar vueltas sin
    /// parar; cualquier otro fallo también se anota y se sigue aceptando.
    /// </summary>
    internal static async Task RunAcceptLoopAsync(Func<CancellationToken, ValueTask<TcpClient>> accept, Action<TcpClient> onAccepted,
        TimeSpan errorDelay, WarningThrottle warnings, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await accept(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ex is SocketException socketError)
                    warnings.Warn("error al aceptar", null, $"Error al aceptar una conexión: {socketError.SocketErrorCode}");
                else
                    warnings.Error("error inesperado al aceptar", null, $"Error inesperado al aceptar una conexión: {ex}");

                try
                {
                    await Task.Delay(errorDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                onAccepted(tcp);
            }
            catch (Exception ex)
            {
                warnings.Error("error inesperado al aceptar", null, $"Error inesperado con una conexión recién aceptada: {ex}");
                Dispose(tcp);
            }
        }
    }

    /// <summary>
    /// Nada más aceptar: mira la IP y los límites por IP (conexiones abiertas y ritmo de conexiones nuevas) antes de
    /// leer nada. Si los pasa, la conexión sigue en su propia tarea.
    /// </summary>
    private void OnAccepted(TcpClient tcp, CancellationToken ct)
    {
        if (!TryGetRemote(tcp, out IPAddress remote))
        {
            Dispose(tcp);
            return;
        }

        if (!_gate.TryEnter(remote, out string refusal))
        {
            _warnings.Warn("conexión rechazada", remote, $"Conexión de {remote} rechazada: {refusal}");
            Dispose(tcp);
            return;
        }

        _ = HandleConnectionAsync(tcp, remote, ct);
    }

    /// <summary>Lee el primer mensaje (con plazo y tamaño máximo) y encamina la conexión según lo que sea.</summary>
    private async Task HandleConnectionAsync(TcpClient tcp, IPAddress remote, CancellationToken ct)
    {
        _connections[tcp] = 0;

        // La conexión cuenta para el límite de su IP hasta que se cierra o hasta que resulta ser el control o un
        // enganche de un servidor (un servidor con muchos jugadores tiene muchos enganches abiertos a la vez).
        int counted = 1;
        void StopCounting()
        {
            if (Interlocked.Exchange(ref counted, 0) != 0)
                _gate.Leave(remote);
        }

        try
        {
            if (!TryPrepare(tcp, out NetworkStream stream))
                return;

            IMessage? first;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_options.FirstMessageTimeout);
                first = await RelayWire.ReadAsync(stream, _options.MaxFirstMessageBytes, deadline.Token);
            }

            switch (first)
            {
                case RelayRegisterMessage register:
                    ServerSession? session = await RegisterAsync(tcp, stream, remote, register, ct);
                    if (session != null)
                    {
                        StopCounting();
                        _prober.Watch(session, ct);
                        await session.RunAsync((server, token) => _broker.SetUdpToken(server.Code, token.SessionId, token.Token), ct);
                        Unregister(session);
                    }

                    break;

                case RelayLookupMessage lookup:
                    await ReplyAndCloseAsync(tcp, stream, BuildLookupResult(lookup), ct);
                    break;

                case RelayJoinMessage join:
                    await _broker.JoinAsync(tcp, stream, remote, join, ct);
                    break;

                case RelayAttachMessage attach:
                    await _broker.AttachAsync(tcp, stream, remote, attach, StopCounting, ct);
                    break;

                case RelayListRequestMessage listRequest:
                    if (listRequest.RelayVersion != ProtocolInfo.RelayVersion)
                    {
                        // La respuesta no tiene campo de error: sin entender la petición, se cierra sin contestar.
                        _warnings.Warn("lista de otra versión", remote, $"Lista pedida desde {remote} con otra versión del relay ({listRequest.RelayVersion}): conexión cerrada");
                        break;
                    }

                    await ReplyAndCloseAsync(tcp, stream, BuildListResponse(), ct);
                    break;

                case null:
                    break;

                default:
                    _warnings.Warn("primer mensaje no admitido", remote, $"Conexión de {remote} cerrada: primer mensaje no admitido ({first.Type})");
                    break;
            }
        }
        catch (Exception ex) when (RelayWire.IsConnectionError(ex))
        {
            // Sin primer mensaje a tiempo, mensaje demasiado grande o ilegible, o la conexión se cortó.
        }
        catch (Exception ex)
        {
            RelayLog.Error($"Error inesperado en una conexión: {ex}");
        }
        finally
        {
            StopCounting();
            Dispose(tcp);
            _connections.TryRemove(tcp, out _);
        }
    }

    /// <summary>
    /// Atiende un <see cref="RelayRegisterMessage"/>. Devuelve la sesión si se aceptó; null si se rechazó
    /// (ya se ha contestado) o si falló el envío.
    /// </summary>
    private async Task<ServerSession?> RegisterAsync(TcpClient tcp, NetworkStream stream, IPAddress remote, RelayRegisterMessage register, CancellationToken ct)
    {
        if (register.RelayVersion != ProtocolInfo.RelayVersion)
        {
            await RejectAsync(tcp, stream, remote, RelayError.VersionMismatch, permanent: true, ct, Lang.T(
                $"Versión del relay incompatible (relay {ProtocolInfo.RelayVersion}, servidor {register.RelayVersion}). Actualiza MultiKerbal.",
                $"Incompatible relay version (relay {ProtocolInfo.RelayVersion}, server {register.RelayVersion}). Update MultiKerbal."));
            return null;
        }

        if (register.GamePort < 1 || register.GamePort > 65535)
        {
            await RejectAsync(tcp, stream, remote, RelayError.InvalidGamePort, permanent: false, ct);
            return null;
        }

        RelayServerInfo info = register.Info;
        RelayError rejection = RelayError.None;
        bool permanent = false;
        bool newKey = false;
        string code = string.Empty;
        ServerSession? replaced = null;
        ServerSession? session = null;

        // Bajo el cerrojo del registro: el baneo y los límites por IP se miran antes de asignar el código (un registro
        // rechazado no debe gastar un código), sin cruzarse con otro registro ni con el comando ban. Un código nuevo
        // solo se reserva en memoria; el disco se escribe después, fuera del cerrojo.
        lock (_registryLock)
        {
            if (_stopping)
                return null;

            bool knownKey = _codes!.TryGetCode(register.Key, out code);
            if (knownKey && _codes.IsBanned(code))
            {
                rejection = RelayError.Banned;
                permanent = true;
            }
            else
            {
                // Si el servidor se cayó y vuelve antes de que caduque, la sesión vieja se sustituye (no cuenta dos veces).
                if (knownKey)
                    _servers.TryGetValue(code, out replaced);

                int sameIp = _servers.Values.Count(s => s != replaced && s.RemoteAddress.Equals(remote));
                if (sameIp >= _options.MaxServersPerIp)
                {
                    rejection = RelayError.TooManyServersFromIp;
                }
                else if (!knownKey && !_newCodes.TryTake(remote))
                {
                    // Cada clave nueva gasta un código para siempre: una IP no puede pedir códigos sin fin.
                    rejection = RelayError.TooManyNewCodes;
                }
                else
                {
                    code = _codes.Reserve(register.Key, out newKey);
                    session = new ServerSession(tcp, code, NewSecret(), info, register.GamePort, remote, _options.HeartbeatTimeout);
                    _servers[code] = session;
                }
            }
        }

        if (session != null && newKey && !TryPersistCode(session))
        {
            session = null;
            replaced = null;
            rejection = RelayError.CodeNotSaved;
        }

        if (session == null)
        {
            await RejectAsync(tcp, stream, remote, rejection, permanent, ct);
            return null;
        }

        if (replaced != null)
        {
            RelayLog.Info($"[{ServerCode.Format(code)}] El servidor vuelve a registrarse desde {remote}: se sustituye la conexión anterior");
            replaced.Close();
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_options.FirstMessageTimeout);
            await session.SendAsync(new RelayRegisteredMessage { Code = code, UdpSecret = session.UdpSecret }, deadline.Token);
        }
        catch (Exception ex) when (RelayWire.IsConnectionError(ex))
        {
            session.Close();
            Unregister(session);
            return null;
        }

        RelayLog.Info($"[{ServerCode.Format(code)}] Servidor registrado: \"{session.Info.Name}\" desde {remote}, puerto de juego {register.GamePort}");
        return session;
    }

    /// <summary>
    /// Guarda en codes.json el código nuevo de una sesión recién registrada, sin el cerrojo del registro. Si no se puede,
    /// la quita del registro y suelta el código (false).
    /// </summary>
    private bool TryPersistCode(ServerSession session)
    {
        try
        {
            _codes!.Persist(session.Code);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RelayLog.Error($"No se pudo guardar el código de un servidor nuevo: {ex.Message}");
        }

        lock (_registryLock)
        {
            if (_servers.TryGetValue(session.Code, out ServerSession? current) && current == session)
                _servers.Remove(session.Code);
        }

        _codes!.Forget(session.Code);
        return false;
    }

    /// <summary>Quita la sesión del registro, salvo que otra más nueva ya la haya sustituido.</summary>
    private void Unregister(ServerSession session)
    {
        bool removed = false;
        lock (_registryLock)
        {
            if (_servers.TryGetValue(session.Code, out ServerSession? current) && current == session)
            {
                _servers.Remove(session.Code);
                removed = true;
            }
        }

        session.Close();
        if (removed && !_stopping)
        {
            bool expired = DateTime.UtcNow - session.LastSeen >= _options.HeartbeatTimeout;
            RelayLog.Info($"[{ServerCode.Format(session.Code)}] Servidor fuera de la lista ({(expired ? "sin latidos" : "conexión cerrada")})");
        }
    }

    private RelayLookupResultMessage BuildLookupResult(RelayLookupMessage lookup)
    {
        if (lookup.RelayVersion != ProtocolInfo.RelayVersion)
            return NotFound(RelayError.VersionMismatch, VersionMismatchText(lookup.RelayVersion));

        if (!TryGetServer(lookup.Code ?? string.Empty, out ServerSession? server))
            return NotFound(RelayError.NoServer, RelayErrorText.Describe(RelayError.NoServer, string.Empty));

        // El nombre y la versión ya llegan limpios y cortados (ServerSession.Clean).
        RelayServerInfo info = server.Info;
        bool direct = server.IsDirect;
        return new RelayLookupResultMessage
        {
            Found = true,
            Name = info.Name,
            GameProtocol = info.GameProtocol,
            ModVersion = info.ModVersion,

            // Solo si la última comprobación pudo llegar por TCP y por UDP; si no, se entra por el relay.
            DirectHost = direct ? server.RemoteAddress.ToString() : string.Empty,
            DirectPort = direct ? server.GamePort : 0,
            ErrorCode = RelayError.None,
            Error = string.Empty,
        };
    }

    /// <summary>
    /// La lista pública: solo servidores vivos que piden salir en ella, con el nombre saneado, ordenados por jugadores
    /// (de más a menos) y luego por nombre (ordinal, sin distinguir mayúsculas), como mucho
    /// <see cref="ProtocolInfo.MaxListedServers"/>. El código sale sin guion, como en el resto del protocolo.
    /// </summary>
    internal RelayListResponseMessage BuildListResponse()
    {
        ServerSession[] sessions;
        lock (_registryLock)
            sessions = _servers.Values.ToArray();

        var entries = new List<PublicServerEntry>();
        foreach (ServerSession session in sessions)
        {
            // Una sola lectura: nombre, jugadores y demás salen del mismo latido.
            RelayServerInfo info = session.Info;
            if (session.IsClosed || !info.IsPublic)
                continue;

            entries.Add(new PublicServerEntry
            {
                Code = session.Code,
                Name = info.Name,
                Players = info.Players,
                MaxPlayers = info.MaxPlayers,
                HasPassword = info.HasPassword,
                GameProtocol = info.GameProtocol,
                ModVersion = info.ModVersion,
                Direct = session.IsDirect,
            });
        }

        PublicServerEntry[] sorted = entries
            .OrderByDescending(e => e.Players)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .Take(ProtocolInfo.MaxListedServers)
            .ToArray();
        return new RelayListResponseMessage { Servers = sorted };
    }

    private static RelayLookupResultMessage NotFound(RelayError error, string text) => new()
    {
        Found = false,
        Name = string.Empty,
        ModVersion = string.Empty,
        DirectHost = string.Empty,
        ErrorCode = error,
        Error = text,
    };

    /// <summary>
    /// Rechaza un registro y cierra. <paramref name="permanent"/> solo para lo que no se arregla solo (otra versión,
    /// baneo): con lo demás, el servidor vuelve a intentarlo pasado un rato. El texto va en el idioma del relay y solo lo
    /// usa un servidor que no conozca el código.
    /// </summary>
    private async Task RejectAsync(TcpClient tcp, NetworkStream stream, IPAddress remote, RelayError error, bool permanent, CancellationToken ct, string? text = null)
    {
        string reason = text ?? RelayErrorText.Describe(error, string.Empty);
        _warnings.Info("registro rechazado", remote, $"Registro rechazado ({remote}): {reason}");
        await ReplyAndCloseAsync(tcp, stream, new RelayRejectedMessage { ErrorCode = error, Permanent = permanent, Reason = reason }, ct);
    }

    private Task ReplyAndCloseAsync(TcpClient tcp, NetworkStream stream, IMessage reply, CancellationToken ct) =>
        RelayWire.ReplyAndCloseAsync(tcp, stream, reply, _options.FirstMessageTimeout, ct);

    /// <summary>
    /// La IP de quien llama (IPv4 normal aunque el socket sea de doble pila). Falla (false) si el otro lado ya se había
    /// ido cuando se aceptó, que es lo único esperable aquí.
    /// </summary>
    private static bool TryGetRemote(TcpClient tcp, out IPAddress remote)
    {
        remote = IPAddress.None;
        try
        {
            if (tcp.Client.RemoteEndPoint is not IPEndPoint endPoint)
                return false;

            remote = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>El flujo de la conexión. Falla (false) si el otro lado ya se ha ido.</summary>
    private static bool TryPrepare(TcpClient tcp, out NetworkStream stream)
    {
        stream = null!;
        try
        {
            tcp.NoDelay = true;
            stream = tcp.GetStream();
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Secreto UDP del servidor: aleatorio y nunca 0 (la ficha 0 es la de las sondas).</summary>
    private static ulong NewSecret()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong secret;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            secret = BitConverter.ToUInt64(bytes);
        }
        while (secret == 0);

        return secret;
    }

    private static void Dispose(TcpClient tcp)
    {
        try
        {
            tcp.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
