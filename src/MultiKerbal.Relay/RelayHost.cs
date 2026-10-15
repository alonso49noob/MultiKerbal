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
        _forwarder = new UdpForwarder(FindServerBySecret);
        _broker = new SessionBroker(options, FindServer, _forwarder);
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

    internal static string NoServerText => Lang.T("No hay ningún servidor encendido con ese código", "There is no server running with that code");

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

    private static TcpListener CreateListener(int port)
    {
        var dualMode = new TcpListener(IPAddress.IPv6Any, port);
        try
        {
            dualMode.Server.DualMode = true;
            dualMode.Start();
            return dualMode;
        }
        catch (SocketException)
        {
            // Sistemas sin IPv6.
            dualMode.Stop();
            var ipv4 = new TcpListener(IPAddress.Any, port);
            ipv4.Start();
            return ipv4;
        }
    }

    private static Socket CreateUdpSocket(int port)
    {
        Socket socket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.DualMode = true;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        catch (SocketException)
        {
            socket.Dispose();
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }

        // En Windows, un aviso ICMP de "puerto inalcanzable" (un jugador que se fue, una sonda a un puerto cerrado)
        // haría fallar la siguiente lectura del socket.
        if (OperatingSystem.IsWindows())
            socket.IOControl(SioUdpConnReset, new byte[4], null);
        return socket;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                RelayLog.Warn($"Error al aceptar una conexión: {ex.SocketErrorCode}");
                continue;
            }

            _ = HandleConnectionAsync(tcp, ct);
        }
    }

    /// <summary>Lee el primer mensaje (con plazo) y encamina la conexión según lo que sea.</summary>
    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        _connections[tcp] = 0;
        try
        {
            if (!TryPrepare(tcp, out IPAddress remote, out NetworkStream stream))
                return;

            IMessage? first;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_options.FirstMessageTimeout);
                first = await RelayWire.ReadAsync(stream, deadline.Token);
            }

            switch (first)
            {
                case RelayRegisterMessage register:
                    ServerSession? session = await RegisterAsync(tcp, stream, remote, register, ct);
                    if (session != null)
                    {
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
                    await _broker.AttachAsync(tcp, stream, remote, attach, ct);
                    break;

                case RelayListRequestMessage listRequest:
                    if (listRequest.RelayVersion != ProtocolInfo.RelayVersion)
                    {
                        // La respuesta no tiene campo de error: sin entender la petición, se cierra sin contestar.
                        RelayLog.Warn($"Lista pedida desde {remote} con otra versión del relay ({listRequest.RelayVersion}): conexión cerrada");
                        break;
                    }

                    await ReplyAndCloseAsync(tcp, stream, BuildListResponse(), ct);
                    break;

                case null:
                    break;

                default:
                    RelayLog.Warn($"Conexión de {remote} cerrada: primer mensaje no admitido ({first.Type})");
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
            await RejectAsync(tcp, stream, remote, Lang.T(
                $"Versión del relay incompatible (relay {ProtocolInfo.RelayVersion}, servidor {register.RelayVersion}). Actualiza MultiKerbal.",
                $"Incompatible relay version (relay {ProtocolInfo.RelayVersion}, server {register.RelayVersion}). Update MultiKerbal."), ct);
            return null;
        }

        if (register.GamePort < 1 || register.GamePort > 65535)
        {
            await RejectAsync(tcp, stream, remote, Lang.T("Puerto de juego inválido", "Invalid game port"), ct);
            return null;
        }

        RelayServerInfo info = register.Info;
        info.Name ??= string.Empty;
        info.ModVersion ??= string.Empty;

        string? rejection = null;
        string code = string.Empty;
        ServerSession? replaced = null;
        ServerSession? session = null;

        // Todo bajo el cerrojo del registro: el límite por IP y el baneo se miran antes de asignar el código
        // (un registro rechazado no debe gastar un código ni escribir en disco) y sin cruzarse con otro registro
        // ni con el comando ban.
        lock (_registryLock)
        {
            if (_stopping)
                return null;

            bool knownKey = _codes!.TryGetCode(register.Key, out code);
            if (knownKey && _codes.IsBanned(code))
            {
                rejection = Lang.T("Este servidor está bloqueado en el relay", "This server is blocked on the relay");
            }
            else
            {
                // Si el servidor se cayó y vuelve antes de que caduque, la sesión vieja se sustituye (no cuenta dos veces).
                if (knownKey)
                    _servers.TryGetValue(code, out replaced);

                int sameIp = _servers.Values.Count(s => s != replaced && s.RemoteAddress.Equals(remote));
                if (sameIp >= _options.MaxServersPerIp)
                {
                    rejection = Lang.T(
                        $"Demasiados servidores desde tu IP (máximo {_options.MaxServersPerIp})",
                        $"Too many servers from your IP (maximum {_options.MaxServersPerIp})");
                }
                else
                {
                    try
                    {
                        code = _codes.GetOrAssign(register.Key);
                        session = new ServerSession(tcp, code, NewSecret(), info, register.GamePort, remote, _options.HeartbeatTimeout);
                        _servers[code] = session;
                    }
                    catch (IOException ex)
                    {
                        RelayLog.Error($"No se pudo guardar el código de un servidor nuevo: {ex.Message}");
                        rejection = Lang.T("El relay no pudo guardar tu código. Inténtalo más tarde.", "The relay could not save your code. Try again later.");
                    }
                }
            }
        }

        if (session == null)
        {
            await RejectAsync(tcp, stream, remote, rejection!, ct);
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

        RelayLog.Info($"[{ServerCode.Format(code)}] Servidor registrado: \"{ListedName.Sanitize(info.Name)}\" desde {remote}, puerto de juego {register.GamePort}");
        return session;
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
            return NotFound(VersionMismatchText(lookup.RelayVersion));

        if (!TryGetServer(lookup.Code ?? string.Empty, out ServerSession? server))
            return NotFound(NoServerText);

        RelayServerInfo info = server.Info;
        bool direct = server.IsDirect;
        return new RelayLookupResultMessage
        {
            Found = true,
            Name = ListedName.Sanitize(info.Name),
            GameProtocol = info.GameProtocol,
            ModVersion = info.ModVersion ?? string.Empty,

            // Solo si la última comprobación pudo llegar por TCP y por UDP; si no, se entra por el relay.
            DirectHost = direct ? server.RemoteAddress.ToString() : string.Empty,
            DirectPort = direct ? server.GamePort : 0,
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
                Name = ListedName.Sanitize(info.Name),
                Players = info.Players,
                MaxPlayers = info.MaxPlayers,
                HasPassword = info.HasPassword,
                GameProtocol = info.GameProtocol,
                ModVersion = info.ModVersion ?? string.Empty,
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

    private static RelayLookupResultMessage NotFound(string error) => new()
    {
        Found = false,
        Name = string.Empty,
        ModVersion = string.Empty,
        DirectHost = string.Empty,
        Error = error,
    };

    private async Task RejectAsync(TcpClient tcp, NetworkStream stream, IPAddress remote, string reason, CancellationToken ct)
    {
        RelayLog.Info($"Registro rechazado ({remote}): {reason}");
        await ReplyAndCloseAsync(tcp, stream, new RelayRejectedMessage { Reason = reason }, ct);
    }

    private Task ReplyAndCloseAsync(TcpClient tcp, NetworkStream stream, IMessage reply, CancellationToken ct) =>
        RelayWire.ReplyAndCloseAsync(tcp, stream, reply, _options.FirstMessageTimeout, ct);

    /// <summary>
    /// Saca la IP de quien llama y el flujo de la conexión. Falla (false) si el otro lado ya se había ido
    /// cuando se aceptó, que es lo único esperable aquí.
    /// </summary>
    private static bool TryPrepare(TcpClient tcp, out IPAddress remote, out NetworkStream stream)
    {
        remote = IPAddress.None;
        stream = null!;
        try
        {
            if (tcp.Client.RemoteEndPoint is not IPEndPoint endPoint)
                return false;

            remote = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
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
