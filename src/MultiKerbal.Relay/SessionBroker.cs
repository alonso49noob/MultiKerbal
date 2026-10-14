using System.Diagnostics;
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
/// dos conexiones, con un tope de tráfico en cada sentido, hasta que una de las dos se cierra.
/// </summary>
internal sealed class SessionBroker
{
    private const int BufferSize = 16 * 1024;

    /// <summary>
    /// Cuando un lado termina limpio, se pasa su FIN al otro y se le da este margen para cerrar por su cuenta.
    /// Así lee todo lo que quedaba (por ejemplo el motivo de una expulsión) en vez de recibir un corte.
    /// </summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly RelayOptions _options;
    private readonly Func<string, ServerSession?> _findServer;
    private readonly object _lock = new();

    /// <summary>Sesiones vivas (esperando el enganche o pasando bytes) por su id. Se toca con <see cref="_lock"/>.</summary>
    private readonly Dictionary<long, RelaySession> _sessions = new();

    /// <param name="findServer">Busca un servidor vivo por su código; null si no hay ninguno.</param>
    public SessionBroker(RelayOptions options, Func<string, ServerSession?> findServer)
    {
        _options = options;
        _findServer = findServer;
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
            await RefuseAsync(tcp, stream, RelayHost.VersionMismatchText(join.RelayVersion), ct);
            return;
        }

        ServerSession? server = _findServer(join.Code ?? string.Empty);
        if (server == null)
        {
            await RefuseAsync(tcp, stream, RelayHost.NoServerText, ct);
            return;
        }

        string code = ServerCode.Format(server.Code);
        RelaySession? session = TryOpen(server.Code);
        if (session == null)
        {
            RelayLog.Info($"[{code}] Jugador {remote} rechazado: ya hay {_options.MaxSessionsPerServer} sesiones por el relay");
            await RefuseAsync(tcp, stream, Lang.T("El servidor está lleno", "The server is full"), ct);
            return;
        }

        try
        {
            Attachment? attachment = await WaitForAttachAsync(session, server, remote, ct);
            if (attachment == null)
            {
                RelayLog.Info($"[{code}] El servidor no se enganchó a tiempo para el jugador {remote}");
                await RefuseAsync(tcp, stream, Lang.T("El servidor no responde a través del relay", "The server is not answering through the relay"), ct);
                return;
            }

            // El "sí" sale antes de pasar ningún byte del servidor, para que el jugador lo lea primero.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_options.FirstMessageTimeout);
                await RelayWire.WriteAsync(stream, new RelayJoinResultMessage { Ok = true, Error = string.Empty }, deadline.Token);
            }

            RelayLog.Info($"[{code}] Jugador {remote} conectado por el relay (sesión {session.Id})");
            await PipeAsync(tcp, stream, attachment.Tcp, attachment.Stream, ct);
            RelayLog.Info($"[{code}] Sesión {session.Id} terminada");
        }
        finally
        {
            Close(session);
        }
    }

    /// <summary>
    /// Atiende la conexión que abre el servidor para una sesión. Si es válida, la conexión pasa a la sesión y esto
    /// vuelve cuando la sesión termina; si no, vuelve enseguida y quien llama la cierra.
    /// </summary>
    public async Task AttachAsync(TcpClient tcp, NetworkStream stream, IPAddress remote, RelayAttachMessage attach, CancellationToken ct)
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
            RelayLog.Warn($"Enganche rechazado desde {remote}: sesión desconocida, caducada o con otro secreto");
            return;
        }

        await session.Finished.Task.WaitAsync(ct);
    }

    private RelaySession? TryOpen(string code)
    {
        lock (_lock)
        {
            if (_sessions.Values.Count(s => s.Code == code) >= _options.MaxSessionsPerServer)
                return null;

            long id;
            do
                id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
            while (id == 0 || _sessions.ContainsKey(id));

            var session = new RelaySession(id, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)), code);
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
            await server.SendAsync(new RelayIncomingMessage
            {
                SessionId = session.Id,
                Secret = session.Secret,
                ClientAddress = remote.ToString(),
            }, deadline.Token);
            return await session.Attach.Task.WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (RelayWire.IsConnectionError(ex) && !ct.IsCancellationRequested)
        {
        }

        // Si un enganche llega justo ahora, TrySetCanceled decide quién gana: si gana el plazo, ya no se admite.
        return session.Attach.TrySetCanceled() ? null : session.Attach.Task.Result;
    }

    /// <summary>Pasa bytes en los dos sentidos hasta que se cierre un lado; luego cierra los dos.</summary>
    private async Task PipeAsync(TcpClient player, NetworkStream playerStream, TcpClient server, NetworkStream serverStream, CancellationToken ct)
    {
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task up = PumpAsync(playerStream, server, serverStream, NewLimiter(), pumps.Token);
        Task down = PumpAsync(serverStream, player, playerStream, NewLimiter(), pumps.Token);
        try
        {
            Task first = await Task.WhenAny(up, down);
            if (first.IsCompletedSuccessfully)
                await Task.WhenAny(first == up ? down : up, Task.Delay(CloseGrace, pumps.Token));
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

        to.Client.Shutdown(SocketShutdown.Send);
    }

    private RateLimiter NewLimiter() => new(_options.BytesPerSecond, () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    /// <summary>Quita la sesión y despierta a quien espera por ella (el enganche, si lo hubo).</summary>
    private void Close(RelaySession session)
    {
        lock (_lock)
            _sessions.Remove(session.Id);

        session.Attach.TrySetCanceled();
        session.Finished.TrySetResult();
    }

    private Task RefuseAsync(TcpClient tcp, NetworkStream stream, string error, CancellationToken ct) =>
        RelayWire.ReplyAndCloseAsync(tcp, stream, new RelayJoinResultMessage { Ok = false, Error = error }, _options.FirstMessageTimeout, ct);

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
        public RelaySession(long id, ulong secret, string code)
        {
            Id = id;
            Secret = secret;
            Code = code;
        }

        public long Id { get; }

        public ulong Secret { get; }

        /// <summary>Código (sin guion) del servidor de la sesión, para el límite de sesiones por servidor.</summary>
        public string Code { get; }

        /// <summary>Se completa con la conexión del servidor, o se cancela si caduca o la sesión termina antes.</summary>
        public TaskCompletionSource<Attachment> Attach { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Se completa cuando la sesión termina: la conexión del servidor ya puede cerrarse.</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
