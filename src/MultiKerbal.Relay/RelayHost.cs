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
/// de servidores (códigos permanentes, latidos, caducidad) y la consulta por código.
/// </summary>
public sealed partial class RelayHost
{
    private readonly RelayOptions _options;
    private readonly object _registryLock = new();

    /// <summary>Servidores vivos por código (sin guion). Se toca siempre con <see cref="_registryLock"/>.</summary>
    private readonly Dictionary<string, ServerSession> _servers = new();

    /// <summary>Todas las conexiones abiertas (también las que aún no han dicho qué son), para cerrarlas al parar.</summary>
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private CodeStore? _codes;
    private TcpListener? _listener;
    private Socket? _probeSocket;
    private bool _stopping;
    private int _stopped;

    internal RelayHost(RelayOptions options)
    {
        _options = options;
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

    /// <summary>Busca un servidor vivo por su código (con o sin guion, en mayúsculas o minúsculas).</summary>
    internal bool TryGetServer(string code, [NotNullWhen(true)] out ServerSession? session)
    {
        session = null;
        if (!ServerCode.TryNormalize(code, out string normalized))
            return false;

        lock (_registryLock)
            return _servers.TryGetValue(normalized, out session);
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
            _probeSocket = CreateUdpSocket(_options.ProbePort);
            ProbePort = ((IPEndPoint)_probeSocket.LocalEndPoint!).Port;
        }
        catch
        {
            Stop();
            throw;
        }

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
            IPAddress remote = RemoteIp(tcp);
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();

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
                        await session.RunAsync(ct);
                        Unregister(session);
                    }

                    break;

                case RelayLookupMessage lookup:
                    await ReplyAndCloseAsync(tcp, stream, BuildLookupResult(lookup), ct);
                    break;

                // La tarea 6 añade aquí RelayJoin (un jugador entra por el relay) y RelayAttach (el servidor
                // abre la conexión de una sesión); la tarea 8 añade RelayListRequest (la lista pública).
                // Hasta entonces, como cualquier otro primer mensaje, se cierra la conexión.
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

        string code;
        try
        {
            code = _codes!.GetOrAssign(register.Key);
        }
        catch (IOException ex)
        {
            RelayLog.Error($"No se pudo guardar el código de un servidor nuevo: {ex.Message}");
            await RejectAsync(tcp, stream, remote, Lang.T("El relay no pudo guardar tu código. Inténtalo más tarde.", "The relay could not save your code. Try again later."), ct);
            return null;
        }

        RelayServerInfo info = register.Info;
        info.Name ??= string.Empty;
        info.ModVersion ??= string.Empty;

        string? rejection = null;
        ServerSession? replaced = null;
        ServerSession? session = null;
        lock (_registryLock)
        {
            if (_stopping)
                return null;

            // El baneo se comprueba aquí dentro para no cruzarse con el comando ban.
            if (_codes!.IsBanned(code))
            {
                rejection = Lang.T("Este servidor está bloqueado en el relay", "This server is blocked on the relay");
            }
            else
            {
                // Si el servidor se cayó y vuelve antes de que caduque, la sesión vieja se sustituye (no cuenta dos veces).
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
                    session = new ServerSession(tcp, code, NewSecret(), info, register.GamePort, remote, _options.HeartbeatTimeout);
                    _servers[code] = session;
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

        RelayLog.Info($"[{ServerCode.Format(code)}] Servidor registrado: \"{info.Name}\" desde {remote}, puerto de juego {register.GamePort}");
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
        {
            return NotFound(Lang.T(
                $"El relay usa otra versión del protocolo (relay {ProtocolInfo.RelayVersion}, tú {lookup.RelayVersion}). Actualiza MultiKerbal.",
                $"The relay uses another protocol version (relay {ProtocolInfo.RelayVersion}, you {lookup.RelayVersion}). Update MultiKerbal."));
        }

        if (!TryGetServer(lookup.Code ?? string.Empty, out ServerSession? server))
            return NotFound(Lang.T("No hay ningún servidor encendido con ese código", "There is no server running with that code"));

        RelayServerInfo info = server.Info;
        return new RelayLookupResultMessage
        {
            Found = true,
            Name = info.Name ?? string.Empty,
            GameProtocol = info.GameProtocol,
            ModVersion = info.ModVersion ?? string.Empty,

            // La tarea 7 rellena la dirección directa cuando la comprobación de acceso la da por alcanzable.
            DirectHost = string.Empty,
            DirectPort = 0,
            Error = string.Empty,
        };
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

    /// <summary>Manda una respuesta y cierra el envío con un FIN normal, para que el otro lado la lea entera.</summary>
    private async Task ReplyAndCloseAsync(TcpClient tcp, NetworkStream stream, IMessage reply, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.FirstMessageTimeout);
        await RelayWire.WriteAsync(stream, reply, deadline.Token);
        try
        {
            tcp.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private static IPAddress RemoteIp(TcpClient tcp)
    {
        IPAddress address = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
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
