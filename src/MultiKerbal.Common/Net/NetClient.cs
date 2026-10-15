using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;
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

        /// <summary>La sesión acabó yendo por el relay (y no directa al servidor).</summary>
        public bool ViaRelay => _session?.ViaRelay ?? false;

        /// <summary>Nombre del servidor según la consulta al relay; null si no se hizo.</summary>
        public string ServerName => _session?.ServerName;

        public void Connect(string host, int port, int timeoutMs = 5000) => Connect(ConnectPlan.Direct(host, port, timeoutMs));

        public void Connect(ConnectPlan plan)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            lock (_gate)
            {
                _session?.Close("Reconectando", raiseEvent: false);
                while (_events.TryDequeue(out _))
                {
                }

                _session = new Session(_events, plan);
                _session.Start();
            }
        }

        public void Send(IMessage message, Delivery delivery = Delivery.Reliable) => _session?.Send(message, delivery);

        /// <summary>Abre el canal UDP con el token recibido en el saludo. Si falla, todo sigue por TCP.</summary>
        public void StartUdp(ulong token) => _session?.StartUdp(token);

        /// <summary>Cierre ordenado: avisa al servidor con el motivo y cierra.</summary>
        public void Disconnect(string reason) => _session?.Shutdown(reason);

        public bool TryDequeue(out NetEvent netEvent) => _events.TryDequeue(out netEvent);

        /// <summary>
        /// Solo para los tests: cierra el socket UDP y abre otro en un puerto nuevo, con la misma ficha, como cuando
        /// el router del jugador le cambia el puerto a mitad de partida.
        /// </summary>
        internal void RebindUdpForTests() => _session?.RebindUdp();

        public void Dispose() => Disconnect("Cliente cerrado");

        private sealed class Session
        {
            private const int UdpBindIntervalMs = 250;
            private const int UdpBindMaxAttempts = 40;
            private const int ShutdownGraceMs = 1000;

            /// <summary>Plazo para conectar con el relay y para que conteste a la consulta.</summary>
            private const int RelayTimeoutMs = 5000;

            /// <summary>Espera del resultado de entrar por el relay: más que su plazo de enganche (10 s).</summary>
            private const int RelayJoinTimeoutMs = 15000;

            /// <summary>Límite de los textos de error que manda el relay (se muestran al jugador).</summary>
            private const int MaxRelayErrorLength = 200;

            // Evita que Windows invalide el socket UDP al recibir un ICMP "puerto inalcanzable".
            private static readonly IOControlCode SioUdpConnReset = (IOControlCode)(-1744830452);

            private readonly ConcurrentQueue<NetEvent> _events;
            private readonly ConnectPlan _plan;
            private readonly ConcurrentQueue<Outgoing> _outgoing = new ConcurrentQueue<Outgoing>();
            private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);

            private TcpClient _tcp;
            private NetworkStream _stream;
            private volatile Socket _udp;
            private IPEndPoint _serverEndPoint;
            private ulong _udpToken;
            private Timer _shutdownTimer;
            private int _closed;
            private int _raiseEvents = 1;
            private long _bytesSent;
            private long _bytesReceived;

            /// <summary>Bytes encolados para TCP que aún no se han escrito en el socket.</summary>
            private long _queuedTcpBytes;
            private volatile NetClientState _state = NetClientState.Connecting;
            private volatile bool _udpStarted;
            private volatile bool _udpReady;
            private volatile string _shutdownReason;
            private volatile bool _viaRelay;
            private volatile string _serverName;

            public Session(ConcurrentQueue<NetEvent> events, ConnectPlan plan)
            {
                _events = events;
                _plan = plan;
            }

            public bool ViaRelay => _viaRelay;

            public string ServerName => _serverName;

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

                // Sin UDP: con el TCP atascado se pierde este estado en vez de retrasar todos los siguientes.
                if (!useUdp && delivery == Delivery.Unreliable && Interlocked.Read(ref _queuedTcpBytes) > ProtocolInfo.MaxQueuedUnreliableBytes)
                    return;

                Enqueue(new Outgoing(data, useUdp));
            }

            private void Enqueue(Outgoing item)
            {
                if (!item.Udp)
                    Interlocked.Add(ref _queuedTcpBytes, item.Data.Length);
                _outgoing.Enqueue(item);
                _sendSignal.Set();
            }

            public void StartUdp(ulong token)
            {
                if (_state != NetClientState.Connected || _udpStarted)
                    return;

                try
                {
                    _udpToken = token;
                    _udp = OpenUdp();
                    _udpStarted = true;

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

            /// <summary>Cambia el socket UDP por otro en un puerto nuevo, con la misma ficha (para los tests).</summary>
            public void RebindUdp()
            {
                if (!_udpStarted || Volatile.Read(ref _closed) != 0)
                    return;

                Socket old = _udp;
                _udp = OpenUdp();
                CloseQuietly(old);
                if (Volatile.Read(ref _closed) != 0)
                    CloseQuietly(_udp);
            }

            /// <summary>Abre un socket UDP "conectado" al servidor (o al relay) y arranca su hilo de recepción.</summary>
            private Socket OpenUdp()
            {
                var udp = new Socket(_serverEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    try
                    {
                        udp.IOControl(SioUdpConnReset, new byte[4], null);
                    }
                    catch (Exception)
                    {
                        // Solo existe en Windows.
                    }

                    udp.Connect(_serverEndPoint);
                    new Thread(() => UdpReceiveLoop(udp)) { IsBackground = true, Name = "MultiKerbal UDP" }.Start();
                    return udp;
                }
                catch
                {
                    CloseQuietly(udp);
                    throw;
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
                byte[] goodbye = FrameCodec.EncodeTcp(new DisconnectMessage { Reason = reason });
                Interlocked.Add(ref _queuedTcpBytes, goodbye.Length);
                _outgoing.Enqueue(new Outgoing(goodbye, false));
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
                CloseQuietly(Volatile.Read(ref _tcp));
                CloseQuietly(_udp);
                _sendSignal.Set();

                Raise(new NetEvent { Type = NetEventType.Disconnected, Reason = _shutdownReason ?? reason });
            }

            private void Run()
            {
                try
                {
                    string failure = ConnectByPlan();
                    if (failure != null)
                    {
                        Close(failure, raiseEvent: true);
                        return;
                    }

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

            /// <summary>
            /// Sigue el plan (diseño del relay, 4.3): consulta, intento directo y, si falla, entrar por el relay.
            /// Devuelve null con la conexión hecha (<see cref="_tcp"/>, <see cref="_stream"/> y
            /// <see cref="_serverEndPoint"/>) o el motivo para cerrar. Los fallos de red sin otra salida se lanzan.
            /// </summary>
            private string ConnectByPlan()
            {
                string host = _plan.Host;
                int port = _plan.Port;
                bool hasRelay = !string.IsNullOrEmpty(_plan.RelayHost);

                if (_plan.LookupFirst)
                {
                    RelayLookupResultMessage lookup;
                    try
                    {
                        lookup = Lookup();
                    }
                    catch (Exception ex) when (IsNetworkError(ex))
                    {
                        return RelayUnreachable();
                    }

                    if (!lookup.Found)
                    {
                        string error = RelayErrorText.Describe(lookup.ErrorCode, ListedName.Sanitize(lookup.Error, MaxRelayErrorLength));
                        return error.Length == 0 ? Lang.T("No se encontró el servidor", "Server not found") : error;
                    }

                    if (lookup.GameProtocol != ProtocolInfo.Version)
                    {
                        string modVersion = ListedName.Sanitize(lookup.ModVersion, ProtocolInfo.MaxListedVersionLength);
                        string version = modVersion.Length == 0 ? lookup.GameProtocol.ToString() : modVersion;
                        return Lang.T($"Ese servidor usa otra versión de MultiKerbal ({version})", $"That server uses another version of MultiKerbal ({version})");
                    }

                    _serverName = lookup.Name;
                    if (!string.IsNullOrEmpty(lookup.DirectHost) && lookup.DirectPort > 0)
                    {
                        host = lookup.DirectHost;
                        port = lookup.DirectPort;
                    }
                }

                if (!string.IsNullOrEmpty(host))
                {
                    try
                    {
                        UseConnection(OpenTcp(host, port, _plan.DirectTimeoutMs));
                        return null;
                    }
                    catch (Exception ex) when (hasRelay && IsNetworkError(ex))
                    {
                        // Sin acceso directo: se prueba por el relay.
                    }
                }

                if (!hasRelay)
                    return Lang.T("No se indicó ningún servidor", "No server given");

                return JoinThroughRelay();
            }

            /// <summary>Pregunta al relay por el código. El relay contesta y cierra.</summary>
            private RelayLookupResultMessage Lookup()
            {
                TcpClient tcp = OpenTcp(_plan.RelayHost, _plan.RelayPort, RelayTimeoutMs);
                try
                {
                    NetworkStream stream = tcp.GetStream();
                    stream.ReadTimeout = RelayTimeoutMs;
                    WriteFrame(stream, new RelayLookupMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = _plan.JoinCode });
                    return ReadRelayReply<RelayLookupResultMessage>(stream);
                }
                finally
                {
                    CloseQuietly(tcp);
                }
            }

            /// <summary>Pide al relay que lo conecte con el servidor. Si dice que sí, esa conexión pasa a ser la del juego.</summary>
            private string JoinThroughRelay()
            {
                TcpClient tcp;
                try
                {
                    tcp = OpenTcp(_plan.RelayHost, _plan.RelayPort, RelayTimeoutMs);
                }
                catch (Exception ex) when (IsNetworkError(ex))
                {
                    return RelayUnreachable();
                }

                NetworkStream stream = tcp.GetStream();
                stream.ReadTimeout = RelayJoinTimeoutMs;
                WriteFrame(stream, new RelayJoinMessage { RelayVersion = ProtocolInfo.RelayVersion, Code = _plan.JoinCode });
                RelayJoinResultMessage result = ReadRelayReply<RelayJoinResultMessage>(stream);
                if (!result.Ok)
                {
                    string error = RelayErrorText.Describe(result.ErrorCode, ListedName.Sanitize(result.Error, MaxRelayErrorLength));
                    return error.Length == 0 ? Lang.T("El relay no pudo conectar con el servidor", "The relay could not reach the server") : error;
                }

                stream.ReadTimeout = Timeout.Infinite;
                _viaRelay = true;
                UseConnection(tcp);
                return null;
            }

            /// <summary>Lee la respuesta del relay, que tiene que ser del tipo esperado.</summary>
            private static T ReadRelayReply<T>(NetworkStream stream)
                where T : class, IMessage
            {
                IMessage reply = FrameCodec.ReadTcpFrame(stream, new byte[FrameCodec.TcpHeaderSize], out _);
                if (reply == null)
                    throw new IOException("El relay cerró la conexión sin contestar");

                return reply as T ?? throw new ProtocolException($"Respuesta inesperada del relay: {reply.Type}");
            }

            private void UseConnection(TcpClient tcp)
            {
                _stream = tcp.GetStream();
                _serverEndPoint = (IPEndPoint)tcp.Client.RemoteEndPoint;
            }

            /// <summary>
            /// Abre una TCP con plazo. Queda en <see cref="_tcp"/> desde el principio, para que <see cref="Close"/> la
            /// corte si se cancela la conexión a medias.
            /// </summary>
            private TcpClient OpenTcp(string host, int port, int timeoutMs)
            {
                IPAddress address = Resolve(host);
                var tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
                Interlocked.Exchange(ref _tcp, tcp);
                try
                {
                    if (Volatile.Read(ref _closed) != 0)
                        throw new ObjectDisposedException(nameof(NetClient));

                    IAsyncResult connect = tcp.BeginConnect(address, port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
                        throw new TimeoutException(Lang.T("El servidor no responde", "The server is not responding"));
                    tcp.EndConnect(connect);
                    return tcp;
                }
                catch
                {
                    CloseQuietly(tcp);
                    throw;
                }
            }

            private static void WriteFrame(NetworkStream stream, IMessage message)
            {
                byte[] frame = FrameCodec.EncodeTcp(message);
                stream.Write(frame, 0, frame.Length);
            }

            private static string RelayUnreachable() => Lang.T(
                "No se puede contactar con el relay: los códigos y la lista no funcionan ahora; puedes conectar por IP",
                "Cannot reach the relay: codes and the server list don't work right now; you can connect by IP");

            private static bool IsNetworkError(Exception ex) =>
                ex is SocketException || ex is IOException || ex is TimeoutException || ex is ObjectDisposedException || ex is ProtocolException;

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
                            {
                                SendDatagram(item.Data);
                            }
                            else
                            {
                                _stream.Write(item.Data, 0, item.Data.Length);
                                Interlocked.Add(ref _queuedTcpBytes, -item.Data.Length);
                            }

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

            private void UdpReceiveLoop(Socket udp)
            {
                var buffer = new byte[65536];
                while (Volatile.Read(ref _closed) == 0)
                {
                    int received;
                    try
                    {
                        received = udp.Receive(buffer);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // Cerrado (al terminar o al cambiarlo por otro).
                        if (Volatile.Read(ref _closed) != 0 || udp != _udp)
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
