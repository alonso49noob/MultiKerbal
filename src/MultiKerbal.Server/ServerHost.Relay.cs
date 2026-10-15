using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;
using MultiKerbal.Server.Net;
using MultiKerbal.Server.Relay;

namespace MultiKerbal.Server;

public sealed partial class ServerHost
{
    private readonly RelayLinkOptions _relayOptions;
    private RelayLink? _relay;

    /// <summary>
    /// Datos que el enlace manda al relay en cada latido. Se cambia entera (nunca se modifica una ya publicada)
    /// cada vez que entra o sale un jugador, porque el enlace la lee desde otro hilo.
    /// </summary>
    private volatile RelayServerInfo _relayInfo = new();

    private string _lastRelayDescription = string.Empty;

    /// <summary>
    /// Último código y acceso directo vistos por el bucle principal (vacío mientras no hay código). Solo se difunde si
    /// cambian, así que los latidos y las recomprobaciones que repiten el mismo resultado no avisan a nadie. Hilo principal.
    /// </summary>
    private string _broadcastCode = string.Empty;

    private bool _broadcastDirect;

    /// <summary>
    /// Las órdenes de Hospedar que llegan de fuera de este equipo (o desconocidas) las puede mandar cualquiera: su aviso se
    /// escribe como mucho una vez por minuto. Hilo principal.
    /// </summary>
    private readonly WarningLimiter _foreignControlWarnings = new(TimeSpan.FromMinutes(1));

    private readonly WarningLimiter _unknownControlWarnings = new(TimeSpan.FromMinutes(1));

    /// <summary>El enlace con el relay; null si está desactivado (para los tests).</summary>
    internal RelayLink? RelayLink => _relay;

    private void StartRelay()
    {
        if (!Config.Relay)
            return;

        UpdateRelayInfo();
        try
        {
            byte[] key = RelayKey.LoadOrCreate(_universe.DirectoryPath);
            _relay = new RelayLink(
                Config.RelayAddress,
                key,
                Config.RelayAnnouncedPortOverride ?? Port,
                () => _relayInfo,
                _network,
                () => _inbound.Enqueue(new ServerEvent(ServerEventKind.RelayChanged)),
                _relayOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Sin clave no hay código estable: el servidor funciona igualmente, solo por IP.
            Log.Error($"No se pudo preparar el relay, se desactiva: {ex.Message}");
            return;
        }

        _relay.Start();
    }

    private void StopRelay() => _relay?.Stop();

    /// <summary>Recalcula los datos para el relay (nombre, jugadores, contraseña...). Hilo principal.</summary>
    private void UpdateRelayInfo()
    {
        _relayInfo = new RelayServerInfo
        {
            Name = Config.ServerName,
            Players = AuthenticatedPlayers().Count(),
            MaxPlayers = Config.MaxPlayers,
            HasPassword = !string.IsNullOrEmpty(Config.Password),
            IsPublic = Config.Public,
            GameProtocol = ProtocolInfo.Version,
            ModVersion = ModVersion,
        };
    }

    private static string ModVersion => typeof(ServerHost).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

    private string DescribeRelay()
    {
        RelayLink? relay = _relay;
        if (relay == null)
            return "Relay desactivado";

        var (state, code, error, direct) = relay.Snapshot();
        switch (state)
        {
            case RelayLinkState.Connecting:
                return $"Conectando con {relay.RelayAddress}...";
            case RelayLinkState.Registered:
                return $"Código {ServerCode.Format(code)} ({(direct ? "accesible directamente" : "por el relay")})";
            case RelayLinkState.Rejected:
                return $"Rechazado: {error}";
            default:
                return "Relay desactivado";
        }
    }

    /// <summary>
    /// El enlace avisa de cada cambio; aquí se registra una vez cada estado distinto y se avisa a los jugadores si
    /// cambió el código o el acceso directo (no en cada vaivén del estado, para no inundarlos).
    /// </summary>
    private void OnRelayChanged()
    {
        string description = DescribeRelay();
        if (description != _lastRelayDescription)
        {
            _lastRelayDescription = description;
            Log.Info($"Relay: {description}");
        }

        if (!TryBuildServerCode(out ServerCodeMessage message))
        {
            // Sin código (conectando de nuevo, rechazado...): cuando vuelva a haberlo, se avisa aunque sea el mismo.
            _broadcastCode = string.Empty;
            _broadcastDirect = false;
            return;
        }

        if (message.Code == _broadcastCode && message.Direct == _broadcastDirect)
            return;

        _broadcastCode = message.Code;
        _broadcastDirect = message.Direct;
        BroadcastReliable(message);
    }

    /// <summary>El código actual del servidor, tal como va por el cable (sin guion). Falso si no está registrado.</summary>
    private bool TryBuildServerCode(out ServerCodeMessage message)
    {
        message = new ServerCodeMessage();
        var (state, code, _, direct) = _relay?.Snapshot() ?? (RelayLinkState.Off, string.Empty, string.Empty, false);
        if (state != RelayLinkState.Registered || code.Length == 0)
            return false;

        message.Code = code;
        message.Direct = direct;
        return true;
    }

    /// <summary>Estado actual del servidor para la consulta del anfitrión. Hilo principal.</summary>
    private ServerStatusResponseMessage BuildStatus()
    {
        var (state, code, error, direct) = _relay?.Snapshot() ?? (RelayLinkState.Off, string.Empty, string.Empty, false);
        return new ServerStatusResponseMessage
        {
            GameProtocol = ProtocolInfo.Version,
            ModVersion = ModVersion,
            Name = Config.ServerName,
            Players = AuthenticatedPlayers().Count(),
            MaxPlayers = Config.MaxPlayers,
            HasPassword = !string.IsNullOrEmpty(Config.Password),
            RelayState = state,
            RelayError = error,
            Code = code,
            Direct = direct,
            IsPublic = Config.Public,
        };
    }

    /// <summary>
    /// Mensajes que se contestan sin haber entrado a la partida: la consulta de estado (la puede hacer cualquiera) y las
    /// órdenes del anfitrión (solo desde este mismo equipo). En los dos casos se contesta y se cierra, sin crear jugador.
    /// Devuelve false si el mensaje no es de esos.
    /// </summary>
    private bool TryAnswerHostingRequest(ClientConnection connection, IMessage message)
    {
        switch (message)
        {
            case ServerStatusRequestMessage:
                _network.Send(connection, BuildStatus());
                _network.Kick(connection, "Consulta de estado");
                return true;

            case ServerControlMessage control:
                if (!connection.IsLoopback)
                {
                    // Una conexión del relay sale del relay, no de este equipo: nadie de fuera manda aquí.
                    if (_foreignControlWarnings.Allow(out int skipped))
                    {
                        Log.Warn($"Orden del anfitrión ignorada: la conexión #{connection.Id} no es de este equipo ({connection.RemoteEndPoint})" +
                                 (skipped > 0 ? $" (y {skipped} más en el último minuto)" : string.Empty));
                    }

                    _network.Kick(connection, "Solo se acepta desde el propio equipo");
                    return true;
                }

                // Una orden desconocida no se contesta: así quien la mandó sabe que no se aplicó.
                if (ApplyControl(control.Action))
                    _network.Send(connection, BuildStatus());
                _network.Kick(connection, "Orden del anfitrión");
                return true;

            default:
                return false;
        }
    }

    /// <summary>Aplica una orden del anfitrión. False si no la conoce (no se ha hecho nada).</summary>
    private bool ApplyControl(ServerControlAction action)
    {
        switch (action)
        {
            case ServerControlAction.Recheck:
                _relay?.Recheck();
                Log.Info("Anfitrión: se repite la comprobación de acceso");
                return true;
            case ServerControlAction.SetPublic:
                SetPublic(true);
                return true;
            case ServerControlAction.SetPrivate:
                SetPublic(false);
                return true;
            default:
                if (_unknownControlWarnings.Allow(out int skipped))
                    Log.Warn($"Orden del anfitrión desconocida: {action}{(skipped > 0 ? $" (y {skipped} más en el último minuto)" : string.Empty)}");
                return false;
        }
    }

    /// <summary>Le dice a un jugador el código del servidor, si ya hay. Hilo principal.</summary>
    private void SendServerCode(Player player)
    {
        if (TryBuildServerCode(out ServerCodeMessage message))
            _network.Send(player.Connection, message);
    }
}
