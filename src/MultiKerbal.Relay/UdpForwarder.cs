using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;

namespace MultiKerbal.Relay;

/// <summary>
/// Reenvío del UDP de los jugadores que entran por el relay (diseño, 4.3 punto 5). Solo mira los 8 primeros bytes
/// de cada datagrama, la ficha, y lo demás lo pasa tal cual:
/// <list type="bullet">
/// <item>Ficha 0: el hola UDP de un servidor, que apunta su dirección UDP.</item>
/// <item>Desde la dirección UDP de un servidor: va a la última dirección conocida del jugador de esa ficha.</item>
/// <item>Si no, con la ficha de una sesión viva: se apunta el remitente como dirección del jugador (siempre la
/// última, por si su router le cambia el puerto) y va a la dirección UDP del servidor.</item>
/// </list>
/// Todo lo demás se descarta en silencio. Los bytes cuentan para el tope de tráfico de la sesión.
/// <para>
/// La dirección UDP de cada servidor se guarda por su código y no se olvida cuando se cae su control: las sesiones
/// de sus jugadores siguen vivas (cada una tiene su propia TCP) y su UDP debe seguir pasando mientras el servidor
/// vuelve a registrarse. Solo la sustituye un hola nuevo del mismo código. Así la dirección de un servidor nunca
/// puede tomarse por la de un jugador.
/// </para>
/// </summary>
internal sealed class UdpForwarder
{
    private readonly Func<ulong, ServerSession?> _findServerBySecret;

    /// <summary>Rutas de las sesiones por su ficha.</summary>
    private readonly ConcurrentDictionary<ulong, UdpRoute> _routes = new();

    /// <summary>Dirección del último hola de cada servidor, por su código. Se cambia con <see cref="_serversLock"/>.</summary>
    private readonly ConcurrentDictionary<string, EndPoint> _endPointByCode = new();

    /// <summary>Lo contrario: código del servidor de cada dirección. Se cambia con <see cref="_serversLock"/>.</summary>
    private readonly ConcurrentDictionary<EndPoint, string> _codeByEndPoint = new();

    private readonly object _serversLock = new();
    private Socket? _socket;

    /// <param name="findServerBySecret">Servidor vivo por su secreto UDP; null si no hay.</param>
    public UdpForwarder(Func<ulong, ServerSession?> findServerBySecret)
    {
        _findServerBySecret = findServerBySecret;
    }

    /// <param name="socket">Socket UDP en el puerto del relay (el mismo número que el TCP).</param>
    public void Start(Socket socket, CancellationToken ct)
    {
        _socket = socket;
        _ = ReceiveLoopAsync(socket, ct);
    }

    /// <summary>Da de alta la ficha de una sesión. False si ya la usa otra sesión.</summary>
    public bool TryAdd(ulong token, UdpRoute route) => _routes.TryAdd(token, route);

    /// <summary>Olvida la ficha (solo si sigue siendo de esa ruta).</summary>
    public void Remove(ulong token, UdpRoute route) => _routes.TryRemove(new KeyValuePair<ulong, UdpRoute>(token, route));

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[65536];
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
                // Por ejemplo, un aviso ICMP de un envío anterior. El socket sigue sirviendo.
                continue;
            }

            try
            {
                Handle(buffer, result.ReceivedBytes, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                // Un datagrama nunca debe parar el reenvío.
                RelayLog.Error($"Error inesperado al reenviar un datagrama UDP: {ex}");
            }
        }
    }

    private void Handle(byte[] buffer, int count, EndPoint sender)
    {
        if (count < FrameCodec.UdpHeaderSize)
            return;

        // Mismo orden de bytes que PacketWriter.WriteUInt64 (el menos significativo primero).
        ulong token = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        if (token == 0)
        {
            HandleHello(buffer, count, sender);
            return;
        }

        if (_codeByEndPoint.TryGetValue(sender, out string? fromServer))
        {
            // Del servidor al jugador: solo con fichas de sus propias sesiones y si el jugador ya ha dado señales.
            // Nunca sigue más abajo: la dirección de un servidor no puede apuntarse como la de un jugador.
            if (_routes.TryGetValue(token, out UdpRoute? down) && down.ServerCode == fromServer
                && down.Player is { } player && down.Down.Reserve(count) == TimeSpan.Zero)
            {
                Send(buffer, count, player);
            }

            return;
        }

        if (!_routes.TryGetValue(token, out UdpRoute? route))
            return;

        route.Player = sender;
        if (_endPointByCode.TryGetValue(route.ServerCode, out EndPoint? server) && route.Up.Reserve(count) == TimeSpan.Zero)
            Send(buffer, count, server);
    }

    /// <summary>
    /// Un hola UDP con el secreto de un servidor vivo, y que sale de la misma IP que su control, apunta desde dónde
    /// llega el UDP de ese servidor.
    /// </summary>
    private void HandleHello(byte[] buffer, int count, EndPoint sender)
    {
        if (sender is not IPEndPoint senderIp
            || !FrameCodec.TryDecodeUdp(buffer, count, out _, out IMessage? message)
            || message is not RelayUdpHelloMessage { UdpSecret: not 0 } hello
            || _findServerBySecret(hello.UdpSecret) is not { } server)
        {
            return;
        }

        IPAddress address = senderIp.Address.IsIPv4MappedToIPv6 ? senderIp.Address.MapToIPv4() : senderIp.Address;
        if (!address.Equals(server.RemoteAddress))
            return;

        server.UdpEndPoint = sender;
        lock (_serversLock)
        {
            if (_endPointByCode.TryGetValue(server.Code, out EndPoint? previous))
            {
                if (previous.Equals(sender))
                    return;
                _codeByEndPoint.TryRemove(new KeyValuePair<EndPoint, string>(previous, server.Code));
            }

            // Si esa dirección era de otro servidor (su router la ha reasignado), deja de serlo.
            if (_codeByEndPoint.TryGetValue(sender, out string? other))
                _endPointByCode.TryRemove(new KeyValuePair<string, EndPoint>(other, sender));

            _endPointByCode[server.Code] = sender;
            _codeByEndPoint[sender] = server.Code;
        }
    }

    private void Send(byte[] buffer, int count, EndPoint target)
    {
        try
        {
            _socket?.SendTo(buffer, 0, count, SocketFlags.None, target);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // UDP es lo que es: se pierde este datagrama.
        }
    }
}

/// <summary>Ruta UDP de una sesión: su servidor, la última dirección del jugador y los topes de tráfico.</summary>
internal sealed class UdpRoute
{
    private volatile EndPoint? _player;

    /// <param name="up">Tope del sentido jugador → servidor (el mismo que el de su TCP).</param>
    /// <param name="down">Tope del sentido servidor → jugador.</param>
    public UdpRoute(string serverCode, RateLimiter up, RateLimiter down)
    {
        ServerCode = serverCode;
        Up = up;
        Down = down;
    }

    /// <summary>Código (sin guion) del servidor de la sesión.</summary>
    public string ServerCode { get; }

    public RateLimiter Up { get; }

    public RateLimiter Down { get; }

    /// <summary>Última dirección desde la que el jugador mandó un datagrama con esta ficha; null hasta el primero.</summary>
    public EndPoint? Player
    {
        get => _player;
        set => _player = value;
    }
}
