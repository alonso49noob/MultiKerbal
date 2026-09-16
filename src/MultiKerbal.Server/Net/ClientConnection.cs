using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MultiKerbal.Server.Net;

/// <summary>Estado de red de un cliente. Compartido entre hilos de E/S y el bucle principal: todo acceso es atómico.</summary>
internal sealed class ClientConnection
{
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private EndPoint? _udpEndPoint;
    private string? _closeReason;
    private double _lastReceived;
    private long _pendingBytes;
    private int _closed;

    public ClientConnection(int id, TcpClient tcp, double now)
    {
        Id = id;
        Tcp = tcp;
        RemoteEndPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        ConnectedAt = now;
        _lastReceived = now;
    }

    public int Id { get; }

    public TcpClient Tcp { get; }

    public string RemoteEndPoint { get; }

    public double ConnectedAt { get; }

    /// <summary>Se asigna una única vez al aceptar el saludo, antes de que pueda llegar UDP.</summary>
    public ulong UdpToken { get; set; }

    public EndPoint? UdpEndPoint
    {
        get => Volatile.Read(ref _udpEndPoint);
        set => Volatile.Write(ref _udpEndPoint, value);
    }

    /// <summary>Motivo comunicado al cerrar tras una expulsión.</summary>
    public string? CloseReason
    {
        get => Volatile.Read(ref _closeReason);
        set => Volatile.Write(ref _closeReason, value);
    }

    public double LastReceived => Volatile.Read(ref _lastReceived);

    public long PendingBytes => Interlocked.Read(ref _pendingBytes);

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public ChannelReader<byte[]> Outgoing => _outgoing.Reader;

    public void MarkReceived(double now) => Volatile.Write(ref _lastReceived, now);

    public bool Enqueue(byte[] frame)
    {
        if (IsClosed)
            return false;

        Interlocked.Add(ref _pendingBytes, frame.Length);
        if (_outgoing.Writer.TryWrite(frame))
            return true;

        Interlocked.Add(ref _pendingBytes, -frame.Length);
        return false;
    }

    public void MarkSent(int bytes) => Interlocked.Add(ref _pendingBytes, -bytes);

    public void CompleteOutgoing() => _outgoing.Writer.TryComplete();

    public bool TryMarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;
}
