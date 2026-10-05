using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Mods;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Time;
using MultiKerbal.Server.Net;
using MultiKerbal.Server.Persistence;
using MultiKerbal.Server.Systems;

namespace MultiKerbal.Server;

/// <summary>
/// Bucle principal del servidor. Toda la lógica de juego corre en un único hilo: los hilos de red
/// solo encolan eventos, así que los sistemas no necesitan cerrojos.
/// </summary>
public sealed partial class ServerHost
{
    private const double TickSeconds = 0.02;
    private const double TimeBroadcastSeconds = 5.0;
    private const double AutosaveSeconds = 60.0;

    private readonly ConcurrentQueue<ServerEvent> _inbound = new();
    private readonly ServerClock _clock = new();
    private readonly NetworkServer _network;
    private readonly UniverseStore _universe;
    private readonly VesselStore _vessels;
    private readonly ModStore _mods;
    private readonly AccountStore _accounts;
    private readonly ProgressSystem _progress;
    private readonly TimeSystem _time = new();
    /// <summary>Por id de conexión (incluye conexiones aún sin saludo).</summary>
    private readonly Dictionary<int, Player> _players = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private Thread? _loopThread;
    private volatile bool _running;
    private int _nextPlayerId = 1;
    private double _lastTimeBroadcast;
    private double _lastAutosave;

    public ServerHost(ServerConfig config)
    {
        config.Validate();
        Config = config;
        _network = new NetworkServer(_inbound, _clock);
        _universe = new UniverseStore(config.DataDirectory);
        _vessels = new VesselStore(_universe.DirectoryPath);
        _mods = new ModStore(_universe.DirectoryPath);
        _accounts = new AccountStore(_universe.DirectoryPath);
        _progress = new ProgressSystem(_universe.DirectoryPath, config.Mode, config.StartingFunds);
    }

    public ServerConfig Config { get; }

    public int Port => _network.Port;

    public void Start()
    {
        UniverseData data = _universe.Load();
        _vessels.Load();
        _mods.Load();
        _accounts.Load();
        _progress.Load();
        double now = _clock.Now;
        _time.Initialize(data.UniversalTime, now);
        if (!Config.PauseClockWhenEmpty)
            _time.Resume(now);

        _network.Start(Config.Port);
        _lastAutosave = now;
        _running = true;
        _loopThread = new Thread(Loop) { Name = "MultiKerbal Server" };
        _loopThread.Start();

        Log.Info($"Servidor \"{Config.ServerName}\" escuchando en el puerto {Port} (TCP y UDP)");
        Log.Info($"Universo en {_universe.DirectoryPath} — {KerbalTime.Format(data.UniversalTime)}, {_vessels.Count} nave(s)");
        Log.Info($"Partida {GameModes.Describe(_progress.Mode)}, {_accounts.Count} cuenta(s) de jugador");
        Log.Info(_mods.HasReference
            ? $"Mods esperados: {_mods.Mods.Length} (de {_mods.Source}), control \"{Config.ModPolicy}\""
            : $"Sin lista de mods: se tomará la del primer jugador (control \"{Config.ModPolicy}\")");
    }

    public void EnqueueCommand(string line) => _inbound.Enqueue(new ServerEvent(ServerEventKind.Command, Text: line));

    public void RequestStop() => _inbound.Enqueue(new ServerEvent(ServerEventKind.Stop));

    /// <summary>Bloquea hasta que el bucle principal termine.</summary>
    public void WaitForExit() => _stopped.Wait();

    public void Stop()
    {
        RequestStop();
        _loopThread?.Join();
    }

    internal static string? ValidateHandshake(HandshakeRequestMessage request, ServerConfig config, IEnumerable<string> connectedNames)
    {
        if (request.ProtocolVersion != ProtocolInfo.Version)
            return Lang.T(
                $"Versión de protocolo incompatible (servidor {ProtocolInfo.Version}, cliente {request.ProtocolVersion}). Usa la misma versión de MultiKerbal.",
                $"Incompatible protocol version (server {ProtocolInfo.Version}, client {request.ProtocolVersion}). Use the same MultiKerbal version.");

        string name = request.PlayerName?.Trim() ?? string.Empty;
        if (name.Length < ProtocolInfo.MinPlayerNameLength || name.Length > ProtocolInfo.MaxPlayerNameLength)
            return Lang.T(
                $"El nombre debe tener entre {ProtocolInfo.MinPlayerNameLength} y {ProtocolInfo.MaxPlayerNameLength} caracteres",
                $"The name must be between {ProtocolInfo.MinPlayerNameLength} and {ProtocolInfo.MaxPlayerNameLength} characters");
        if (!PlayerNameRegex().IsMatch(name))
            return Lang.T(
                "El nombre solo puede contener letras, números, espacios y los signos _ - .",
                "The name can only contain letters, numbers, spaces and _ - .");

        if (!string.IsNullOrEmpty(config.Password) && request.Password != config.Password)
            return Lang.T("Contraseña del servidor incorrecta", "Wrong server password");

        if (string.IsNullOrEmpty(request.AccountPassword))
            return Lang.T(
                "Escribe tu contraseña de jugador. Si es la primera vez que entras con este nombre, será la de tu cuenta.",
                "Enter your player password. If this is your first time with this name, it becomes your account's password.");
        if (request.AccountPassword.Length > ProtocolInfo.MaxAccountPasswordLength)
            return Lang.T("La contraseña de jugador es demasiado larga", "The player password is too long");

        List<string> names = connectedNames.ToList();
        if (names.Count >= config.MaxPlayers)
            return Lang.T($"El servidor está lleno ({config.MaxPlayers} jugadores)", $"The server is full ({config.MaxPlayers} players)");
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return Lang.T($"Ya hay un jugador conectado con el nombre \"{name}\"", $"A player named \"{name}\" is already connected");

        return null;
    }

    internal static string SanitizeText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c))
                builder.Append(c);
        }

        string result = builder.ToString().Trim();
        return result.Length > maxLength ? result[..maxLength] : result;
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_\-\. ]+$")]
    private static partial Regex PlayerNameRegex();

    private IEnumerable<Player> AuthenticatedPlayers() => _players.Values.Where(p => p.Authenticated);

    private void Loop()
    {
        try
        {
            while (_running)
            {
                double frameStart = _clock.Now;
                while (_running && _inbound.TryDequeue(out ServerEvent serverEvent))
                    HandleEvent(serverEvent);

                Tick(_clock.Now);

                double remaining = TickSeconds - (_clock.Now - frameStart);
                if (remaining > 0)
                    Thread.Sleep(TimeSpan.FromSeconds(remaining));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error fatal en el bucle del servidor: {ex}");
        }
        finally
        {
            Shutdown();
            _stopped.Set();
        }
    }

    private void Shutdown()
    {
        Log.Info("Deteniendo el servidor...");
        foreach (Player player in _players.Values)
            _network.Kick(player.Connection, "El servidor se ha detenido");

        Thread.Sleep(300); // Margen para vaciar las colas de envío.
        _network.Stop();
        SaveUniverse();
        Log.Info("Servidor detenido");
    }

    private void HandleEvent(ServerEvent serverEvent)
    {
        try
        {
            switch (serverEvent.Kind)
            {
                case ServerEventKind.Connected:
                    OnConnected(serverEvent.Connection!);
                    break;
                case ServerEventKind.Disconnected:
                    OnDisconnected(serverEvent.Connection!, serverEvent.Text ?? "Desconectado");
                    break;
                case ServerEventKind.Message:
                    OnMessage(serverEvent.Connection!, serverEvent.Message!);
                    break;
                case ServerEventKind.Command:
                    ExecuteCommand(serverEvent.Text ?? string.Empty);
                    break;
                case ServerEventKind.Stop:
                    _running = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error procesando {serverEvent.Kind} ({serverEvent.Message?.Type}): {ex}");
            if (serverEvent.Connection != null)
                _network.Kick(serverEvent.Connection, "Error interno del servidor");
        }
    }

    private void OnConnected(ClientConnection connection)
    {
        _players[connection.Id] = new Player(connection);
        Log.Info($"Conexión #{connection.Id} desde {connection.RemoteEndPoint}");
    }

    private void OnDisconnected(ClientConnection connection, string reason)
    {
        if (!_players.Remove(connection.Id, out Player? player))
            return;

        if (!player.Authenticated)
        {
            Log.Info($"Conexión #{connection.Id} cerrada antes del saludo: {reason}");
            return;
        }

        double now = _clock.Now;
        Log.Info($"{player.Name} se ha desconectado: {reason}");
        ReleaseVesselsOf(player);
        BroadcastReliable(new PlayerLeftMessage { PlayerId = player.Info.Id });
        SendSystemChat(Lang.T($"{player.Name} ha salido de la partida", $"{player.Name} left the game"));
        RecomputeWarp(now);

        if (!AuthenticatedPlayers().Any() && Config.PauseClockWhenEmpty)
        {
            _time.Pause(now);
            SaveUniverse();
            Log.Info($"Sin jugadores: reloj detenido en {KerbalTime.Format(_time.CurrentUniversalTime(now))}");
        }
    }

    private void OnMessage(ClientConnection connection, IMessage message)
    {
        if (!_players.TryGetValue(connection.Id, out Player? player))
            return;

        if (!player.Authenticated)
        {
            if (message is HandshakeRequestMessage handshake)
                OnHandshake(player, handshake);
            else
                _network.Kick(connection, "Se esperaba el saludo inicial");
            return;
        }

        switch (message)
        {
            case ChatMessage chat:
                OnChat(player, chat);
                break;
            case WarpRequestMessage warp:
                OnWarpRequest(player, warp);
                break;
            case PlayerStatusMessage status:
                OnPlayerStatus(player, status);
                break;
            case VesselProtoMessage proto:
                OnVesselProto(player, proto);
                break;
            case VesselUpdateMessage update:
                OnVesselUpdate(player, update);
                break;
            case VesselRemoveMessage remove:
                OnVesselRemove(player, remove);
                break;
            case VesselControlRequestMessage control:
                OnVesselControlRequest(player, control);
                break;
            case VesselOwnerRequestMessage owner:
                OnVesselOwnerRequest(player, owner);
                break;
            case VesselHandoverRequestMessage handover:
                OnVesselHandoverRequest(player, handover);
                break;
            case VesselHandoverGrantMessage grant:
                OnVesselHandoverGrant(player, grant);
                break;
            case VesselCopilotMessage copilot:
                OnVesselCopilot(player, copilot);
                break;
            case VesselInputMessage input:
                OnVesselInput(player, input);
                break;
            case VesselActionMessage action:
                OnVesselAction(player, action);
                break;
            case VesselLendRequestMessage lend:
                OnVesselLendRequest(player, lend);
                break;
            case ProgressChangeMessage change:
                OnProgressChange(player, change);
                break;
            case DisconnectMessage disconnect:
                string reason = SanitizeText(disconnect.Reason, 100);
                _network.Close(connection, reason.Length > 0 ? reason : "Salió del juego");
                break;
            default:
                Log.Warn($"{player.Name} envió un mensaje inesperado: {message.Type}");
                break;
        }
    }

    private void OnHandshake(Player player, HandshakeRequestMessage request)
    {
        string? rejection = ValidateHandshake(request, Config, AuthenticatedPlayers().Select(p => p.Name));
        string name = request.PlayerName?.Trim() ?? string.Empty;
        bool newAccount = false;
        if (rejection == null)
            rejection = CheckAccount(name, request.AccountPassword, out newAccount);
        List<ModDifference> modDifferences = CheckMods(request, ref rejection);
        if (rejection != null)
        {
            _network.Send(player.Connection, new HandshakeResponseMessage
            {
                ProtocolVersion = ProtocolInfo.Version,
                Accepted = false,
                RejectReason = rejection,
                Mods = _mods.Mods,
            });
            _network.Kick(player.Connection, rejection);
            Log.Info($"Conexión #{player.Connection.Id} rechazada: {rejection}");
            return;
        }

        // La cuenta se crea solo si se acepta todo lo demás (por ejemplo, los mods en modo estricto).
        if (newAccount)
        {
            _accounts.Register(name, request.AccountPassword);
            Log.Info($"Nueva cuenta de jugador: {name}");
        }
        else
        {
            name = _accounts.RegisteredName(name) ?? name;
            _accounts.TouchLogin(name);
        }

        double now = _clock.Now;
        bool firstPlayer = !AuthenticatedPlayers().Any();
        int playerId = _nextPlayerId++;

        player.Authenticated = true;
        player.Info = new PlayerInfo
        {
            Id = playerId,
            Name = name,
            // Proporción áurea: tonos bien repartidos sin importar cuántos jugadores haya.
            ColorHue = (float)(playerId * 0.618033988749895 % 1.0),
            Activity = PlayerActivity.Loading,
            Detail = string.Empty,
        };
        player.Vote = new WarpVote { PlayerName = player.Name, Participating = false, Rate = 1.0, Mode = WarpMode.Rails };
        player.Mods = request.Mods ?? [];
        _network.RegisterUdpToken(player.Connection, CreateUdpToken());

        if (firstPlayer)
            _time.Resume(now);

        _network.Send(player.Connection, new HandshakeResponseMessage
        {
            ProtocolVersion = ProtocolInfo.Version,
            Accepted = true,
            PlayerId = playerId,
            UdpToken = player.Connection.UdpToken,
            ServerName = Config.ServerName,
            Motd = Config.Motd,
            NewAccount = newAccount,
            GameMode = _progress.Mode,
            Mods = _mods.Mods,
        });
        _network.Send(player.Connection, new PlayerListMessage { Players = AuthenticatedPlayers().Select(p => p.Info).ToArray() });
        _network.Send(player.Connection, _time.BuildState(now));
        if (_progress.Mode != GameMode.Sandbox)
            _network.Send(player.Connection, _progress.BuildState());
        SendVesselsTo(player);
        BroadcastReliable(new PlayerJoinedMessage { Player = player.Info }, except: player);
        SendSystemChat(Lang.T($"{player.Name} se ha unido a la partida", $"{player.Name} joined the game"));

        Log.Info($"{player.Name} (jugador {playerId}) se ha unido desde {player.Connection.RemoteEndPoint} — MultiKerbal {request.ModVersion}, KSP {request.GameVersion}");
        AnnounceMods(player, modDifferences);
    }

    /// <summary>
    /// Null si puede entrar. Un nombre sin cuenta se registra con la contraseña que traiga (si es válida);
    /// uno con cuenta solo entra con su contraseña.
    /// </summary>
    private string? CheckAccount(string name, string? password, out bool newAccount)
    {
        newAccount = false;
        switch (_accounts.Check(name, password))
        {
            case AccountCheck.Valid:
                return null;
            case AccountCheck.NotRegistered:
                if (!AccountStore.IsValidNewPassword(password))
                    return Lang.T(
                        $"Para crear tu cuenta, la contraseña de jugador debe tener al menos {ProtocolInfo.MinAccountPasswordLength} caracteres",
                        $"To create your account, the player password must be at least {ProtocolInfo.MinAccountPasswordLength} characters long");
                newAccount = true;
                return null;
            default:
                Log.Info($"Contraseña de jugador incorrecta para {name}");
                return Lang.T(
                    $"La contraseña de jugador de \"{name}\" no es esa. Si la has olvidado, pide al administrador que borre tu cuenta.",
                    $"That is not the player password for \"{name}\". If you forgot it, ask the server admin to delete your account.");
        }
    }

    /// <summary>
    /// Compara los mods del que entra con los que espera el servidor. El primer jugador fija la lista.
    /// Con <c>strict</c> rellena el motivo de rechazo; con <c>warn</c> solo devuelve las diferencias.
    /// </summary>
    private List<ModDifference> CheckMods(HandshakeRequestMessage request, ref string? rejection)
    {
        if (rejection != null || Config.ModPolicy == "off")
            return [];

        if (!_mods.HasReference)
        {
            if (request.Mods is { Length: > 0 })
            {
                _mods.Set(request.Mods, ServerHost.SanitizeText(request.PlayerName, ProtocolInfo.MaxPlayerNameLength));
                Log.Info($"Lista de mods tomada de {_mods.Source}: {_mods.Mods.Length} mod(s)");
            }

            return [];
        }

        List<ModDifference> differences = ModCompare.Compare(_mods.Mods, request.Mods);
        if (Config.ModPolicy == "strict" && differences.Any(d => d.IsProblem))
            rejection = Lang.T(
                $"Tus mods no coinciden con los del servidor: {ModCompare.Summarize(differences)}",
                $"Your mods do not match the server's: {ModCompare.Summarize(differences)}");

        return differences;
    }

    private void AnnounceMods(Player player, List<ModDifference> differences)
    {
        if (differences.Count == 0 || !differences.Any(d => d.IsProblem))
            return;

        string summary = ModCompare.Summarize(differences);
        Log.Warn($"Mods de {player.Name}: {summary}");
        SendSystemChat(Lang.T(
            $"Los mods de {player.Name} no coinciden con los del servidor: {summary}",
            $"{player.Name}'s mods do not match the server's: {summary}"));
    }

    private void OnChat(Player player, ChatMessage chat)
    {
        string text = SanitizeText(chat.Text, ProtocolInfo.MaxChatLength);
        if (text.Length == 0)
            return;

        Log.Chat($"{player.Name}: {text}");
        BroadcastReliable(new ChatMessage { SenderId = player.Info.Id, Text = text });
    }

    private void OnWarpRequest(Player player, WarpRequestMessage request)
    {
        double rate = double.IsFinite(request.Rate) ? Math.Clamp(request.Rate, 1.0, 1e7) : 1.0;
        player.Vote = new WarpVote
        {
            PlayerName = player.Name,
            Participating = request.Participating,
            Rate = rate,
            Mode = request.Mode == WarpMode.Physics ? WarpMode.Physics : WarpMode.Rails,
            AcceptUpTo = double.IsFinite(request.AcceptUpTo) ? Math.Clamp(request.AcceptUpTo, 1.0, 1e7) : 1.0,
            AutoDeny = request.AutoDeny,
        };
        RecomputeWarp(_clock.Now);
    }

    private void OnPlayerStatus(Player player, PlayerStatusMessage status)
    {
        player.Info.Activity = Enum.IsDefined(status.Activity) ? status.Activity : PlayerActivity.Loading;
        player.Info.Detail = SanitizeText(status.Detail, 64);
        BroadcastReliable(new PlayerStatusMessage
        {
            PlayerId = player.Info.Id,
            Activity = player.Info.Activity,
            Detail = player.Info.Detail,
        });
    }

    private void OnProgressChange(Player player, ProgressChangeMessage change)
    {
        if (!_progress.Apply(change))
        {
            // No vale en este modo o no cambia nada: se le devuelve el total para que se alinee.
            if (_progress.Mode != GameMode.Sandbox)
                _network.Send(player.Connection, _progress.BuildState());
            return;
        }

        if (change.Kind == ProgressKind.Tech)
            Log.Info($"{player.Name} ha investigado {change.TechId}");
        BroadcastReliable(_progress.BuildState());
    }

    private void RecomputeWarp(double now)
    {
        double previousRate = _time.Rate;
        WarpMode previousMode = _time.Mode;
        WarpConsensusResult result = WarpConsensus.Compute(AuthenticatedPlayers().Select(p => p.Vote));
        if (!_time.ApplyConsensus(result, now))
            return;

        BroadcastTimeState(now);

        // Si solo cambió quién limita el warp no se registra: pasa cada vez que alguien pide o suelta warp.
        if (result.Rate == previousRate && result.Mode == previousMode)
            return;

        Log.Info(result.Rate > 1.0
            ? $"Warp x{result.Rate:0.##} ({(result.Mode == WarpMode.Physics ? "físico" : "sobre raíles")})"
            : "Tiempo a x1");
    }

    private void Tick(double now)
    {
        foreach (Player player in _players.Values)
        {
            ClientConnection connection = player.Connection;
            if (connection.IsClosed || connection.CloseReason != null)
                continue;

            if (!player.Authenticated && now - connection.ConnectedAt > ProtocolInfo.HandshakeTimeoutSeconds)
                _network.Kick(connection, "Tiempo de saludo agotado");
            else if (now - connection.LastReceived > ProtocolInfo.TimeoutSeconds)
                _network.Close(connection, "Tiempo de espera agotado");
        }

        if (now - _lastTimeBroadcast >= TimeBroadcastSeconds && AuthenticatedPlayers().Any())
            BroadcastTimeState(now);

        if (now - _lastAutosave >= AutosaveSeconds)
        {
            SaveUniverse();
            _lastAutosave = now;
        }
    }

    private void BroadcastTimeState(double now)
    {
        _lastTimeBroadcast = now;
        BroadcastReliable(_time.BuildState(now));
    }

    private void BroadcastReliable(IMessage message, Player? except = null)
    {
        byte[] frame = FrameCodec.EncodeTcp(message);
        foreach (Player player in _players.Values)
        {
            if (player.Authenticated && player != except)
                _network.SendFrame(player.Connection, frame);
        }
    }

    private void SendSystemChat(string text)
    {
        BroadcastReliable(new ChatMessage { SenderId = ChatMessage.SystemSenderId, Text = text });
    }

    private void SaveUniverse()
    {
        try
        {
            _universe.Save(new UniverseData { UniversalTime = _time.CurrentUniversalTime(_clock.Now) });
            _vessels.SaveDirty();
            _progress.Save();
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo guardar el universo: {ex.Message}");
        }
    }

    private static ulong CreateUdpToken()
    {
        ulong token;
        do
        {
            token = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        }
        while (token == 0);

        return token;
    }
}
