using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Server.Net;

/// <summary>
/// Acepta conexiones y hace la E/S asíncrona. No contiene lógica de juego: todo lo recibido se encola
/// como <see cref="ServerEvent"/> para el bucle principal, que corre en un único hilo.
/// </summary>
internal sealed class NetworkServer
{
    private const long MaxPendingBytes = 64L * 1024 * 1024;
    private const int SioUdpConnReset = -1744830452;
    private static readonly TimeSpan KickGrace = TimeSpan.FromSeconds(2);

    private readonly ConcurrentQueue<ServerEvent> _inbound;
    private readonly ServerClock _clock;
    private readonly ConcurrentDictionary<int, ClientConnection> _connections = new();
    private readonly ConcurrentDictionary<ulong, ClientConnection> _byToken = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Socket? _udp;
    private int _nextConnectionId;
    private long _bytesSent;
    private long _bytesReceived;

    public NetworkServer(ConcurrentQueue<ServerEvent> inbound, ServerClock clock)
    {
        _inbound = inbound;
        _clock = clock;
    }

    public int Port { get; private set; }

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public void Start(int port)
    {
        _listener = CreateListener(port);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _udp = CreateUdpSocket(Port);

        _ = AcceptLoopAsync(_cts.Token);
        _ = UdpReceiveLoopAsync(_udp, _cts.Token);
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        _udp?.Dispose();
        foreach (ClientConnection connection in _connections.Values)
            Close(connection, "Servidor detenido");
    }

    public void RegisterUdpToken(ClientConnection connection, ulong token)
    {
        connection.UdpToken = token;
        _byToken[token] = connection;
    }

    public void Send(ClientConnection connection, IMessage message, Delivery delivery = Delivery.Reliable)
    {
        if (connection.IsClosed)
            return;

        if (delivery == Delivery.Unreliable && connection.UdpEndPoint is { } endPoint && _udp != null)
        {
            byte[] datagram = FrameCodec.EncodeUdp(message, connection.UdpToken);
            if (datagram.Length <= ProtocolInfo.MaxUdpDatagramBytes)
            {
                try
                {
                    _udp.SendTo(datagram, SocketFlags.None, endPoint);
                    Interlocked.Add(ref _bytesSent, datagram.Length);
                    return;
                }
                catch (SocketException)
                {
                    // Cae a TCP.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        SendFrame(connection, FrameCodec.EncodeTcp(message));
    }

    /// <summary>Envía una trama TCP ya codificada (para difundir serializando una sola vez).</summary>
    public void SendFrame(ClientConnection connection, byte[] frame)
    {
        if (!connection.Enqueue(frame))
            return;

        if (connection.PendingBytes > MaxPendingBytes)
            Close(connection, "El cliente no recibe los datos a tiempo");
    }

    /// <summary>Envía un aviso de desconexión y cierra cuando se haya vaciado la cola de envío.</summary>
    public void Kick(ClientConnection connection, string reason)
    {
        if (connection.IsClosed || connection.CloseReason != null)
            return;

        connection.CloseReason = reason;
        SendFrame(connection, FrameCodec.EncodeTcp(new DisconnectMessage { Reason = reason }));
        connection.CompleteOutgoing();
    }

    public void Close(ClientConnection connection, string reason)
    {
        if (!connection.TryMarkClosed())
            return;

        connection.CompleteOutgoing();
        try
        {
            connection.Tcp.Close();
        }
        catch (Exception)
        {
        }

        _connections.TryRemove(connection.Id, out _);
        if (connection.UdpToken != 0)
            _byToken.TryRemove(connection.UdpToken, out _);

        _inbound.Enqueue(new ServerEvent(ServerEventKind.Disconnected, connection, Text: connection.CloseReason ?? reason));
    }

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
                Log.Warn($"Error al aceptar una conexión: {ex.SocketErrorCode}");
                continue;
            }

            tcp.NoDelay = true;
            var connection = new ClientConnection(Interlocked.Increment(ref _nextConnectionId), tcp, _clock.Now);
            _connections[connection.Id] = connection;
            _inbound.Enqueue(new ServerEvent(ServerEventKind.Connected, connection));
            _ = ReceiveLoopAsync(connection, ct);
            _ = SendLoopAsync(connection, ct);
        }
    }

    private async Task ReceiveLoopAsync(ClientConnection connection, CancellationToken ct)
    {
        string reason = "Conexión cerrada por el cliente";
        try
        {
            NetworkStream stream = connection.Tcp.GetStream();
            var header = new byte[FrameCodec.TcpHeaderSize];
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct);
                FrameCodec.ParseTcpHeader(header, out int payloadLength, out ushort type);
                var payload = new byte[payloadLength];
                await stream.ReadExactlyAsync(payload, ct);

                connection.MarkReceived(_clock.Now);
                Interlocked.Add(ref _bytesReceived, FrameCodec.TcpHeaderSize + payloadLength);

                IMessage message = FrameCodec.DecodePayload(type, payload, 0, payloadLength);
                if (message is PingMessage ping)
                {
                    // Responder aquí, sin pasar por la cola del bucle principal, para no inflar la latencia medida.
                    Send(connection, new PongMessage { ClientTime = ping.ClientTime, ServerTime = _clock.Now });
                    continue;
                }

                _inbound.Enqueue(new ServerEvent(ServerEventKind.Message, connection, message));
            }
        }
        catch (EndOfStreamException)
        {
        }
        catch (ProtocolException ex)
        {
            reason = $"Error de protocolo: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            reason = "Conexión perdida";
        }

        Close(connection, reason);
    }

    private async Task SendLoopAsync(ClientConnection connection, CancellationToken ct)
    {
        try
        {
            NetworkStream stream = connection.Tcp.GetStream();
            await foreach (byte[] frame in connection.Outgoing.ReadAllAsync(ct))
            {
                await stream.WriteAsync(frame, ct);
                connection.MarkSent(frame.Length);
                Interlocked.Add(ref _bytesSent, frame.Length);
            }

            if (connection.CloseReason != null && !connection.IsClosed)
            {
                // Expulsión: FIN tras vaciar la cola. Si el cliente no cierra a tiempo, se fuerza.
                connection.Tcp.Client.Shutdown(SocketShutdown.Send);
                await Task.Delay(KickGrace, ct);
                Close(connection, connection.CloseReason);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            Close(connection, "Error al enviar datos");
        }
    }

    private async Task UdpReceiveLoopAsync(Socket udp, CancellationToken ct)
    {
        var buffer = new byte[65536];
        EndPoint anyEndPoint = udp.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await udp.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (!FrameCodec.TryDecodeUdp(buffer, result.ReceivedBytes, out ulong token, out IMessage? message) || message is null)
                continue;
            if (!_byToken.TryGetValue(token, out ClientConnection? connection) || connection.IsClosed)
                continue;

            Interlocked.Add(ref _bytesReceived, result.ReceivedBytes);
            connection.MarkReceived(_clock.Now);
            connection.UdpEndPoint = result.RemoteEndPoint;

            if (message is UdpBindMessage)
            {
                Send(connection, new UdpBindAckMessage(), Delivery.Unreliable);
                continue;
            }

            _inbound.Enqueue(new ServerEvent(ServerEventKind.Message, connection, message));
        }
    }
}
