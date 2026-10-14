using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;
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
                Port,
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
            ModVersion = typeof(ServerHost).Assembly.GetName().Version?.ToString(3) ?? string.Empty,
        };
    }

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

    /// <summary>El enlace avisa de cada cambio; aquí se registra una vez cada estado distinto.</summary>
    private void OnRelayChanged()
    {
        string description = DescribeRelay();
        if (description == _lastRelayDescription)
            return;

        _lastRelayDescription = description;
        Log.Info($"Relay: {description}");
    }
}
