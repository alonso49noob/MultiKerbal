using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Net
{
    public enum NetClientState
    {
        Disconnected,
        Connecting,
        Connected,
    }

    public enum Delivery
    {
        /// <summary>TCP: ordenado y garantizado.</summary>
        Reliable,

        /// <summary>UDP si está disponible (si no, TCP). Para estados que se reemplazan continuamente.</summary>
        Unreliable,
    }

    public enum NetEventType
    {
        Connected,
        Disconnected,
        Message,
    }

    public sealed class NetEvent
    {
        public NetEventType Type;
        public IMessage Message;
        public string Reason;
    }

    /// <summary>
    /// Cliente TCP+UDP con hilos propios (sin async, para no depender del contexto de Unity).
    /// Los eventos se encolan y se consumen desde el hilo principal con <see cref="TryDequeue"/>.
    /// </summary>
    public sealed class NetClient : IDisposable
    {
        private readonly ConcurrentQueue<NetEvent> _events = new ConcurrentQueue<NetEvent>();
        private readonly object _gate = new object();
        private Session _session;

        public NetClientState State => _session?.State ?? NetClientState.Disconnected;

        public bool UdpReady => _session?.UdpReady ?? false;

        public long BytesSent => _session?.BytesSent ?? 0;

        public long BytesReceived => _session?.BytesReceived ?? 0;

        public void Connect(string host, int port, int timeoutMs = 5000)
        {
            lock (_gate)
            {
                _session?.Close("Reconectando", raiseEvent: false);
                while (_events.TryDequeue(out _))
                {
                }

                _session = new Session(_events, host, port, timeoutMs);
                _session.Start();
            }
        }

        public void Send(IMessage message, Delivery delivery = Delivery.Reliable) => _session?.Send(message, delivery);

        /// <summary>Abre el canal UDP con el token recibido en el saludo. Si falla, todo sigue por TCP.</summary>
        public void StartUdp(ulong token) => _session?.StartUdp(token);

        /// <summary>Cierre ordenado: avisa al servidor con el motivo y cierra.</summary>
        public void Disconnect(string reason) => _session?.Shutdown(reason);

        public bool TryDequeue(out NetEvent netEvent) => _events.TryDequeue(out netEvent);

        public void Dispose() => Disconnect("Cliente cerrado");

        private sealed class Session
        {
            private const int UdpBindIntervalMs = 250;
            private const int UdpBindMaxAttempts = 40;
            private const int ShutdownGraceMs = 1000;

            // Evita que Windows invalide el socket UDP al recibir un ICMP "puerto inalcanzable".
            private static readonly IOControlCode SioUdpConnReset = (IOControlCode)(-1744830452);

            private readonly ConcurrentQueue<NetEvent> _events;
            private readonly string _host;
            private readonly int _port;
            private readonly int _timeoutMs;
            private readonly ConcurrentQueue<Outgoing> _outgoing = new ConcurrentQueue<Outgoing>();
            private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);

            private TcpClient _tcp;
            private NetworkStream _stream;
            private Socket _udp;
            private IPEndPoint _serverEndPoint;
            private ulong _udpToken;
            private Timer _shutdownTimer;
            private int _closed;
            private int _raiseEvents = 1;
            private long _bytesSent;
            private long _bytesReceived;
            private volatile NetClientState _state = NetClientState.Connecting;
            private volatile bool _udpStarted;
            private volatile bool _udpReady;
            private volatile string _shutdownReason;

            public Session(ConcurrentQueue<NetEvent> events, string host, int port, int timeoutMs)
            {
                _events = events;
                _host = host;
                _port = port;
                _timeoutMs = timeoutMs;
            }

            public NetClientState State => _state;

            public bool UdpReady => _udpReady;

            public long BytesSent => Interlocked.Read(ref _bytesSent);

            public long BytesReceived => Interlocked.Read(ref _bytesReceived);

            public void Start()
            {
                new Thread(Run) { IsBackground = true, Name = "MultiKerbal TCP" }.Start();
            }

            public void Send(IMessage message, Delivery delivery)
            {
                if (_state != NetClientState.Connected || _shutdownReason != null)
                    return;

                bool useUdp = delivery == Delivery.Unreliable && _udpReady;
                byte[] data = useUdp ? FrameCodec.EncodeUdp(message, _udpToken) : FrameCodec.EncodeTcp(message);
                if (useUdp && data.Length > ProtocolInfo.MaxUdpDatagramBytes)
                {
                    useUdp = false;
                    data = FrameCodec.EncodeTcp(message);
                }

                _outgoing.Enqueue(new Outgoing(data, useUdp));
                _sendSignal.Set();
            }

            public void StartUdp(ulong token)
            {
                if (_state != NetClientState.Connected || _udpStarted)
                    return;

                try
                {
                    _udp = new Socket(_serverEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    try
                    {
                        _udp.IOControl(SioUdpConnReset, new byte[4], null);
                    }
                    catch (Exception)
                    {
                        // Solo existe en Windows.
                    }

                    _udp.Connect(_serverEndPoint);
                    _udpToken = token;
                    _udpStarted = true;
                    new Thread(UdpReceiveLoop) { IsBackground = true, Name = "MultiKerbal UDP" }.Start();

                    if (Volatile.Read(ref _closed) != 0)
                        CloseQuietly(_udp);
                }
                catch (Exception)
                {
                    CloseQuietly(_udp);
                    _udp = null;
                    _udpStarted = false;
                }
            }

            public void Shutdown(string reason)
            {
                if (_state != NetClientState.Connected)
                {
                    Close(reason, raiseEvent: true);
                    return;
                }

                if (_shutdownReason != null)
                    return;

                // Encolar antes de marcar: el hilo de envío cierra en cuanto ve el motivo y la cola vacía.
                _outgoing.Enqueue(new Outgoing(FrameCodec.EncodeTcp(new DisconnectMessage { Reason = reason }), false));
                _shutdownReason = reason;
                _sendSignal.Set();
                _shutdownTimer = new Timer(_ => Close(reason, raiseEvent: true), null, ShutdownGraceMs, Timeout.Infinite);
            }

            public void Close(string reason, bool raiseEvent)
            {
                if (!raiseEvent)
                    Interlocked.Exchange(ref _raiseEvents, 0);
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                    return;

                _state = NetClientState.Disconnected;
                _udpReady = false;
                try
                {
                    _shutdownTimer?.Dispose();
                }
                catch (Exception)
                {
                }

                CloseQuietly(_stream);
                CloseQuietly(_tcp);
                CloseQuietly(_udp);
                _sendSignal.Set();

                Raise(new NetEvent { Type = NetEventType.Disconnected, Reason = _shutdownReason ?? reason });
            }

            private void Run()
            {
                try
                {
                    IPAddress address = Resolve(_host);
                    _tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
                    IAsyncResult connect = _tcp.BeginConnect(address, _port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(_timeoutMs))
                        throw new TimeoutException("El servidor no responde");
                    _tcp.EndConnect(connect);

                    _stream = _tcp.GetStream();
                    _serverEndPoint = new IPEndPoint(address, _port);
                    if (Volatile.Read(ref _closed) != 0)
                    {
                        CloseQuietly(_tcp);
                        return;
                    }

                    _state = NetClientState.Connected;
                    Raise(new NetEvent { Type = NetEventType.Connected });
                    new Thread(SendLoop) { IsBackground = true, Name = "MultiKerbal Send" }.Start();

                    ReceiveLoop();
                    Close(Lang.T("El servidor cerró la conexión", "The server closed the connection"), raiseEvent: true);
                }
                catch (Exception ex)
                {
                    Close(Describe(ex), raiseEvent: true);
                }
            }

            private void ReceiveLoop()
            {
                var header = new byte[FrameCodec.TcpHeaderSize];
                while (Volatile.Read(ref _closed) == 0)
                {
                    IMessage message = FrameCodec.ReadTcpFrame(_stream, header, out int frameBytes);
                    if (message == null)
                        return;

                    Interlocked.Add(ref _bytesReceived, frameBytes);
                    if (message is DisconnectMessage disconnect)
                    {
                        Close(string.IsNullOrEmpty(disconnect.Reason) ? "Desconectado por el servidor" : disconnect.Reason, raiseEvent: true);
                        return;
                    }

                    Raise(new NetEvent { Type = NetEventType.Message, Message = message });
                }
            }

            private void SendLoop()
            {
                var bindTimer = Stopwatch.StartNew();
                int bindAttempts = 0;
                try
                {
                    while (Volatile.Read(ref _closed) == 0)
                    {
                        _sendSignal.WaitOne(UdpBindIntervalMs);

                        while (_outgoing.TryDequeue(out Outgoing item))
                        {
                            if (item.Udp)
                                SendDatagram(item.Data);
                            else
                                _stream.Write(item.Data, 0, item.Data.Length);
                            Interlocked.Add(ref _bytesSent, item.Data.Length);
                        }

                        if (_shutdownReason != null)
                        {
                            // FIN tras el aviso; el hilo de recepción cierra cuando el servidor corte (o el temporizador).
                            _tcp.Client.Shutdown(SocketShutdown.Send);
                            return;
                        }

                        if (_udpStarted && !_udpReady && bindAttempts < UdpBindMaxAttempts && bindTimer.ElapsedMilliseconds >= UdpBindIntervalMs)
                        {
                            SendDatagram(FrameCodec.EncodeUdp(new UdpBindMessage(), _udpToken));
                            bindAttempts++;
                            bindTimer.Reset();
                            bindTimer.Start();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Close(Describe(ex), raiseEvent: true);
                }
            }

            private void UdpReceiveLoop()
            {
                var buffer = new byte[65536];
                while (Volatile.Read(ref _closed) == 0)
                {
                    int received;
                    try
                    {
                        received = _udp.Receive(buffer);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        if (Volatile.Read(ref _closed) != 0)
                            return;
                        Thread.Sleep(10);
                        continue;
                    }

                    if (!FrameCodec.TryDecodeUdp(buffer, received, out ulong token, out IMessage message) || token != _udpToken)
                        continue;

                    Interlocked.Add(ref _bytesReceived, received);
                    if (message is UdpBindAckMessage)
                    {
                        _udpReady = true;
                        continue;
                    }

                    Raise(new NetEvent { Type = NetEventType.Message, Message = message });
                }
            }

            private void SendDatagram(byte[] data)
            {
                try
                {
                    _udp?.Send(data);
                }
                catch (SocketException)
                {
                    // UDP es best-effort.
                }
                catch (ObjectDisposedException)
                {
                }
            }

            private void Raise(NetEvent netEvent)
            {
                if (Volatile.Read(ref _raiseEvents) == 1)
                    _events.Enqueue(netEvent);
            }

            private static IPAddress Resolve(string host)
            {
                if (IPAddress.TryParse(host, out IPAddress parsed))
                    return parsed;

                IPAddress[] addresses = Dns.GetHostAddresses(host);
                foreach (IPAddress address in addresses)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                        return address;
                }

                if (addresses.Length > 0)
                    return addresses[0];
                throw new SocketException((int)SocketError.HostNotFound);
            }

            private static string Describe(Exception ex)
            {
                switch (ex)
                {
                    case SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused:
                        return Lang.T("Conexión rechazada: ¿está el servidor en marcha?", "Connection refused: is the server running?");
                    case SocketException socket when socket.SocketErrorCode == SocketError.HostNotFound:
                        return Lang.T("No se encontró el servidor", "Server not found");
                    case SocketException socket:
                        return Lang.T($"Error de red: {socket.SocketErrorCode}", $"Network error: {socket.SocketErrorCode}");
                    case ProtocolException _:
                        return $"Error de protocolo: {ex.Message}";
                    case IOException _:
                    case ObjectDisposedException _:
                        return Lang.T("Conexión perdida", "Connection lost");
                    default:
                        return ex.Message;
                }
            }

            private static void CloseQuietly(IDisposable disposable)
            {
                try
                {
                    disposable?.Dispose();
                }
                catch (Exception)
                {
                }
            }

            private struct Outgoing
            {
                public readonly byte[] Data;
                public readonly bool Udp;

                public Outgoing(byte[] data, bool udp)
                {
                    Data = data;
                    Udp = udp;
                }
            }
        }
    }
}
