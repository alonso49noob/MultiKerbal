using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;

namespace MultiKerbal.Relay;

/// <summary>
/// Jugadores que entran por el relay (diseño, 4.3). El jugador manda <see cref="RelayJoinMessage"/>; el relay crea
/// una sesión y se lo avisa al servidor por su control con <see cref="RelayIncomingMessage"/>; el servidor abre una
/// conexión nueva y manda <see cref="RelayAttachMessage"/>. Desde ahí el relay pasa los bytes tal cual entre las
/// dos conexiones, con un tope de tráfico en cada sentido, hasta que una de las dos se cierra. El UDP de la sesión
/// (por <see cref="UdpForwarder"/>) comparte esos topes, y su ficha se olvida al cerrarse la sesión.
/// </summary>
internal sealed class SessionBroker
{
    private const int BufferSize = 16 * 1024;

    private readonly RelayOptions _options;
    private readonly Func<string, ServerSession?> _findServer;
    private readonly UdpForwarder _udp;
    private readonly WarningThrottle _warnings;
    private readonly object _lock = new();

    /// <summary>Sesiones vivas (esperando el enganche o pasando bytes) por su id. Se toca con <see cref="_lock"/>.</summary>
    private readonly Dictionary<long, RelaySession> _sessions = new();

    /// <param name="findServer">Busca un servidor vivo por su código; null si no hay ninguno.</param>
    /// <param name="udp">Reenvío UDP, donde se dan de alta y de baja las fichas de las sesiones.</param>
    /// <param name="warnings">Avisos de cosas que cualquiera puede provocar (como mucho uno por intervalo, tipo e IP).</param>
    public SessionBroker(RelayOptions options, Func<string, ServerSession?> findServer, UdpForwarder udp, WarningThrottle warnings)
    {
        _options = options;
        _findServer = findServer;
        _udp = udp;
        _warnings = warnings;
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _sessions.Count;
        }
    }

    /// <summary>Atiende la conexión de un jugador que mandó <see cref="RelayJoinMessage"/>. Vuelve cuando la sesión termina.</summary>
    public async Task JoinAsync(TcpClient tcp, NetworkStream stream, IPAddress remote, RelayJoinMessage join, CancellationToken ct)
    {
        if (join.RelayVersion != ProtocolInfo.RelayVersion)
        {
            await RefuseAsync(tcp, stream, RelayError.VersionMismatch, RelayHost.VersionMismatchText(join.RelayVersion), ct);
            return;
        }

        ServerSession? server = _findServer(join.Code ?? string.Empty);
        if (server == null)
        {
            await RefuseAsync(tcp, stream, RelayError.NoServer, ct);
            return;
        }

        string code = ServerCode.Format(server.Code);
        RelaySession? session = TryOpen(server.Code, remote, out RelayError refusal);
        if (session == null)
        {
            _warnings.Info("sesión rechazada", remote, refusal == RelayError.ServerFull
                ? $"[{code}] Jugador {remote} rechazado: ya hay {_options.MaxSessionsPerServer} sesiones por el relay"
                : $"[{code}] Jugador {remote} rechazado: ya tiene {_options.MaxSessionsPerPlayerIp} sesiones con este servidor");
            await RefuseAsync(tcp, stream, refusal, ct);
            return;
        }

        try
        {
            Attachment? attachment = await WaitForAttachAsync(session, server, remote, ct);
            if (attachment == null)
            {
                _warnings.Info("sin enganche", remote, $"[{code}] El servidor no se enganchó a tiempo para el jugador {remote}");
                await RefuseAsync(tcp, stream, RelayError.ServerNotAnswering, ct);
                return;
            }

            // El "sí" sale antes de pasar ningún byte del servidor, para que el jugador lo lea primero.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_options.FirstMessageTimeout);
                await RelayWire.WriteAsync(stream, new RelayJoinResultMessage { Ok = true, ErrorCode = RelayError.None, Error = string.Empty }, deadline.Token);
            }

            RelayLog.Info($"[{code}] Jugador {remote} conectado por el relay (sesión {session.Id})");
            await PipeAsync(session, tcp, stream, attachment.Tcp, attachment.Stream, ct);
            RelayLog.Info($"[{code}] Sesión {session.Id} terminada");
        }
        finally
        {
            Close(session);
        }
    }

    /// <summary>
    /// Atiende la conexión que abre el servidor para una sesión. Si es válida, la conexión pasa a la sesión y esto
    /// vuelve cuando la sesión termina; si no, vuelve enseguida y quien llama la cierra. <paramref name="accepted"/> se
    /// llama en cuanto el enganche se acepta.
    /// </summary>
    public async Task AttachAsync(TcpClient tcp, NetworkStream stream, IPAddress remote, RelayAttachMessage attach, Action accepted, CancellationToken ct)
    {
        RelaySession? session = null;
        if (attach.RelayVersion == ProtocolInfo.RelayVersion)
        {
            lock (_lock)
                _sessions.TryGetValue(attach.SessionId, out session);
        }

        // TrySetResult falla si la sesión ya caducó o ya tenía enganche: solo vale el primero, y a tiempo.
        if (session == null || session.Secret != attach.Secret || !session.Attach.TrySetResult(new Attachment(tcp, stream)))
        {
            _warnings.Warn("enganche rechazado", remote, $"Enganche rechazado desde {remote}: sesión desconocida, caducada o con otro secreto");
            return;
        }

        accepted();
        await session.Finished.Task.WaitAsync(ct);
    }

    /// <summary>
    /// El servidor <paramref name="serverCode"/> avisa de la ficha UDP que dio al jugador de una sesión. Solo vale
    /// para sesiones vivas de ese mismo servidor; una ficha nueva sustituye a la anterior de la sesión.
    /// </summary>
    public void SetUdpToken(string serverCode, long sessionId, ulong token)
    {
        lock (_lock)
        {
            if (token == 0 || !_sessions.TryGetValue(sessionId, out RelaySession? session) || session.Code != serverCode)
            {
                RelayLog.Warn($"[{ServerCode.Format(serverCode)}] Ficha UDP ignorada: la sesión {sessionId} no existe o no es de este servidor");
                return;
            }

            if (session.UdpToken == token)
                return;

            var route = new UdpRoute(session.Code, session.Up, session.Down);
            if (!_udp.TryAdd(token, route))
            {
                RelayLog.Warn($"[{ServerCode.Format(serverCode)}] Ficha UDP ignorada: ya la usa otra sesión");
                return;
            }

            if (session.UdpRoute != null)
                _udp.Remove(session.UdpToken, session.UdpRoute);
            session.UdpToken = token;
            session.UdpRoute = route;
        }
    }

    /// <summary>
    /// Abre una sesión con el servidor <paramref name="code"/> si no está lleno y si el jugador no tiene ya demasiadas
    /// con él: así una sola IP no puede ocupar todas las plazas de un servidor y dejar fuera a los demás.
    /// </summary>
    private RelaySession? TryOpen(string code, IPAddress player, out RelayError refusal)
    {
        lock (_lock)
        {
            int forServer = 0;
            int fromPlayer = 0;
            foreach (RelaySession open in _sessions.Values)
            {
                if (open.Code != code)
                    continue;

                forServer++;
                if (open.Player.Equals(player))
                    fromPlayer++;
            }

            if (forServer >= _options.MaxSessionsPerServer)
            {
                refusal = RelayError.ServerFull;
                return null;
            }

            if (fromPlayer >= _options.MaxSessionsPerPlayerIp)
            {
                refusal = RelayError.TooManySessionsFromIp;
                return null;
            }

            refusal = RelayError.None;
            long id;
            do
                id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
            while (id == 0 || _sessions.ContainsKey(id));

            var session = new RelaySession(id, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)), code, player, NewLimiter(), NewLimiter());
            _sessions[id] = session;
            return session;
        }
    }

    /// <summary>
    /// Avisa al servidor y espera su enganche dentro de <see cref="RelayOptions.AttachTimeout"/>. Null si el aviso
    /// no sale (control caído) o el servidor no se engancha a tiempo.
    /// </summary>
    private async Task<Attachment?> WaitForAttachAsync(RelaySession session, ServerSession server, IPAddress remote, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.AttachTimeout);
        try
        {
            // El plazo solo corta la espera del turno de escritura y la de aquí: una trama empezada sigue hasta el
            // final aunque se deje de esperar, para no estropear el control (si se atasca, la sesión se cierra sola).
            Task incoming = server.SendAsync(new RelayIncomingMessage
            {
                SessionId = session.Id,
                Secret = session.Secret,
                ClientAddress = remote.ToString(),
            }, deadline.Token);
            _ = incoming.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await incoming.WaitAsync(deadline.Token);
            return await session.Attach.Task.WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (RelayWire.IsConnectionError(ex) && !ct.IsCancellationRequested)
        {
        }

        // Si un enganche llega justo ahora, TrySetCanceled decide quién gana: si gana el plazo, ya no se admite.
        return session.Attach.TrySetCanceled() ? null : session.Attach.Task.Result;
    }

    /// <summary>Pasa bytes en los dos sentidos hasta que se cierre un lado; luego cierra los dos.</summary>
    private async Task PipeAsync(RelaySession session, TcpClient player, NetworkStream playerStream, TcpClient server, NetworkStream serverStream, CancellationToken ct)
    {
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task up = PumpAsync(playerStream, server, serverStream, session.Up, pumps.Token);
        Task down = PumpAsync(serverStream, player, playerStream, session.Down, pumps.Token);
        try
        {
            Task first = await Task.WhenAny(up, down);
            if (first.IsCompletedSuccessfully)
                await Task.WhenAny(first == up ? down : up, Task.Delay(_options.CloseGrace, pumps.Token));
        }
        finally
        {
            pumps.Cancel();
            Dispose(player);
            Dispose(server);
            try
            {
                await Task.WhenAll(up, down);
            }
            catch (Exception ex) when (RelayWire.IsConnectionError(ex))
            {
            }
        }
    }

    /// <summary>Copia un sentido, al ritmo que permita el limitador. Al terminar limpio, pasa el FIN al otro lado.</summary>
    private static async Task PumpAsync(NetworkStream from, TcpClient to, NetworkStream toStream, RateLimiter limiter, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read = await from.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            for (TimeSpan wait = limiter.Reserve(read); wait > TimeSpan.Zero; wait = limiter.Reserve(read))
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, wait.TotalMilliseconds)), ct);

            await toStream.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        try
        {
            to.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or NullReferenceException)
        {
            // El otro lado ya se cerró (y su socket se liberó) mientras tanto.
        }
    }

    private RateLimiter NewLimiter() => new(_options.BytesPerSecond, RateLimiter.Now);

    /// <summary>Quita la sesión, olvida su ficha UDP y despierta a quien espera por ella (el enganche, si lo hubo).</summary>
    private void Close(RelaySession session)
    {
        lock (_lock)
        {
            _sessions.Remove(session.Id);
            if (session.UdpRoute != null)
                _udp.Remove(session.UdpToken, session.UdpRoute);
        }

        session.Attach.TrySetCanceled();
        session.Finished.TrySetResult();
    }

    private Task RefuseAsync(TcpClient tcp, NetworkStream stream, RelayError error, CancellationToken ct) =>
        RefuseAsync(tcp, stream, error, RelayErrorText.Describe(error, string.Empty), ct);

    /// <summary>Contesta que no y cierra. El texto (en el idioma del relay) solo lo usa un mod que no conozca el código.</summary>
    private Task RefuseAsync(TcpClient tcp, NetworkStream stream, RelayError error, string text, CancellationToken ct) =>
        RelayWire.ReplyAndCloseAsync(tcp, stream, new RelayJoinResultMessage { Ok = false, ErrorCode = error, Error = text }, _options.FirstMessageTimeout, ct);

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

    private sealed record Attachment(TcpClient Tcp, NetworkStream Stream);

    private sealed class RelaySession
    {
        public RelaySession(long id, ulong secret, string code, IPAddress player, RateLimiter up, RateLimiter down)
        {
            Id = id;
            Secret = secret;
            Code = code;
            Player = player;
            Up = up;
            Down = down;
        }

        public long Id { get; }

        public ulong Secret { get; }

        /// <summary>Código (sin guion) del servidor de la sesión, para el límite de sesiones por servidor.</summary>
        public string Code { get; }

        /// <summary>IP del jugador, para el límite de sesiones por jugador y servidor.</summary>
        public IPAddress Player { get; }

        /// <summary>Tope del sentido jugador → servidor, para la TCP y el UDP.</summary>
        public RateLimiter Up { get; }

        /// <summary>Tope del sentido servidor → jugador, para la TCP y el UDP.</summary>
        public RateLimiter Down { get; }

        /// <summary>Ficha UDP que dio el servidor (0 si aún no). Se toca con el cerrojo del broker.</summary>
        public ulong UdpToken { get; set; }

        /// <summary>Ruta UDP dada de alta con <see cref="UdpToken"/>; null si no hay. Se toca con el cerrojo del broker.</summary>
        public UdpRoute? UdpRoute { get; set; }

        /// <summary>Se completa con la conexión del servidor, o se cancela si caduca o la sesión termina antes.</summary>
        public TaskCompletionSource<Attachment> Attach { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Se completa cuando la sesión termina: la conexión del servidor ya puede cerrarse.</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
