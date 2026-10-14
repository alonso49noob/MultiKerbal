using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Relay;

namespace MultiKerbal.Relay;

/// <summary>
/// Comprobación de acceso directo (diseño, 4.2): ¿se puede llegar al puerto de juego del servidor desde fuera, por
/// TCP y por UDP? Se hace al registrarse, cada <see cref="RelayOptions.RecheckInterval"/> y cuando el servidor la pide.
/// El UDP sale de un socket propio, distinto del de reenvío, para que el agujero que abre el hola del servidor en su
/// router no dé un falso positivo. Corre en sus propias tareas, sin cerrojos durante la red.
/// </summary>
internal sealed class ReachabilityProber
{
    private const int UdpAttempts = 3;

    private readonly RelayOptions _options;

    /// <summary>Sondas UDP esperando respuesta, por su nonce.</summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _pending = new();
    private Socket? _socket;
    private int _probeCount;

    public ReachabilityProber(RelayOptions options)
    {
        _options = options;
    }

    /// <summary>Comprobaciones terminadas (con el resultado ya mandado, o intentado mandar, al servidor).</summary>
    public int ProbeCount => Volatile.Read(ref _probeCount);

    /// <param name="socket">Socket UDP de comprobaciones (<see cref="RelayOptions.ProbePort"/>).</param>
    public void Start(Socket socket, CancellationToken ct)
    {
        _socket = socket;
        _ = ReceiveLoopAsync(socket, ct);
    }

    /// <summary>Comprueba el servidor ahora y luego cada intervalo o cuando lo pida, hasta que su sesión se cierre.</summary>
    public void Watch(ServerSession session, CancellationToken ct) => _ = WatchAsync(session, ct);

    private async Task WatchAsync(ServerSession session, CancellationToken ct)
    {
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct, session.ClosedToken);
        try
        {
            bool first = true;
            while (!watch.IsCancellationRequested)
            {
                await ProbeAsync(session, first, watch.Token);
                first = false;
                await session.WaitForRecheckAsync(_options.RecheckInterval, watch.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RelayLog.Error($"[{ServerCode.Format(session.Code)}] Error inesperado en la comprobación de acceso: {ex}");
        }
    }

    private async Task ProbeAsync(ServerSession session, bool first, CancellationToken ct)
    {
        try
        {
            var target = new IPEndPoint(session.RemoteAddress, session.GamePort);
            Task<bool> tcp = ProbeTcpAsync(target, NewNonce(), ct);
            Task<bool> udp = ProbeUdpAsync(target, NewNonce(), ct);
            bool[] results = await Task.WhenAll(tcp, udp);
            ct.ThrowIfCancellationRequested();

            bool changed = first || session.DirectTcp != results[0] || session.DirectUdp != results[1];
            session.DirectTcp = results[0];
            session.DirectUdp = results[1];
            if (changed)
            {
                RelayLog.Info($"[{ServerCode.Format(session.Code)}] Acceso directo: TCP {(results[0] ? "sí" : "no")}, " +
                              $"UDP {(results[1] ? "sí" : "no")} ({target})");
            }

            try
            {
                await session.SendAsync(new RelayReachabilityMessage { Tcp = results[0], Udp = results[1] }, ct);
            }
            catch (Exception ex) when (RelayWire.IsConnectionError(ex))
            {
                // El control se ha cerrado: la sesión se va y la vigilancia termina con ella.
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                Interlocked.Increment(ref _probeCount);
        }
    }

    /// <summary>Abre una TCP al puerto de juego, manda <see cref="RelayProbeMessage"/> y espera la respuesta con el mismo nonce.</summary>
    private async Task<bool> ProbeTcpAsync(IPEndPoint target, ulong nonce, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.ProbeTimeout);
        using var tcp = new TcpClient(target.AddressFamily);
        try
        {
            await tcp.ConnectAsync(target, deadline.Token);
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();
            await RelayWire.WriteAsync(stream, new RelayProbeMessage { Nonce = nonce }, deadline.Token);
            return await RelayWire.ReadAsync(stream, deadline.Token) is RelayProbeReplyMessage reply && reply.Nonce == nonce;
        }
        catch (Exception ex) when (RelayWire.IsConnectionError(ex))
        {
            return false;
        }
    }

    /// <summary>Manda la sonda UDP (ficha 0) desde el socket de comprobaciones, con varios intentos repartidos en el plazo.</summary>
    private async Task<bool> ProbeUdpAsync(IPEndPoint target, ulong nonce, CancellationToken ct)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = answered;
        try
        {
            byte[] datagram = FrameCodec.EncodeUdp(new RelayProbeMessage { Nonce = nonce }, 0);
            TimeSpan step = _options.ProbeTimeout / UdpAttempts;
            for (int attempt = 0; attempt < UdpAttempts && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    if (_socket == null)
                        return false;
                    _socket.SendTo(datagram, SocketFlags.None, target);
                }
                catch (SocketException)
                {
                    // Sin ruta o con un ICMP pendiente: cuenta como intento fallido.
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (await Task.WhenAny(answered.Task, Task.Delay(step, ct)) == answered.Task)
                    return true;
            }

            return false;
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    /// <summary>Recoge las respuestas UDP de los servidores y despierta a la sonda de ese nonce.</summary>
    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[2048];
        EndPoint anyEndPoint = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (FrameCodec.TryDecodeUdp(buffer, result.ReceivedBytes, out ulong token, out IMessage? message)
                && token == 0
                && message is RelayProbeReplyMessage reply
                && _pending.TryGetValue(reply.Nonce, out TaskCompletionSource? answered))
            {
                answered.TrySetResult();
            }
        }
    }

    /// <summary>Nonce aleatorio y distinto de 0.</summary>
    private static ulong NewNonce()
    {
        ulong nonce;
        do
            nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        while (nonce == 0);

        return nonce;
    }
}
