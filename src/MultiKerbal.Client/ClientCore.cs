using System;
using System.Collections.Generic;
using MultiKerbal.Client.Systems;
using MultiKerbal.Client.UI;
using MultiKerbal.Client.Vessels;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Mods;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Relay;
using MultiKerbal.Common.Time;
using UnityEngine;

namespace MultiKerbal.Client
{
    internal enum SessionState
    {
        Disconnected,
        Connecting,
        Handshaking,

        /// <summary>Saludo aceptado: cargando la partida o jugando.</summary>
        Joined,
    }

    /// <summary>Estado de la sesión multijugador y reparto de mensajes a los sistemas. Todo corre en el hilo de Unity.</summary>
    internal sealed class ClientCore
    {
        public static readonly string ModVersion = typeof(ClientCore).Assembly.GetName().Version.ToString(3);

        private static readonly Color SystemColor = new Color(1f, 0.85f, 0.4f);

        private readonly NetClient _net = new NetClient();
        private readonly StatusReporter _status = new StatusReporter();
        private readonly ToolbarButton _toolbar;
        private string _password = string.Empty;
        private double _nextPing;
        private bool _startGamePending;
        private bool _enteredGame;
        private bool _shutDown;

        public ClientCore()
        {
            Settings = ClientSettings.Load();
            Loc.Initialize(Settings.Language);
            TimeSync = new TimeSyncSystem(Clock);
            Warp = new WarpSystem(
                Clock,
                message => Send(message),
                () => LocalPlayerName,
                () => Settings.Warp,
                () => Vessels != null && Vessels.SpectatingId != Guid.Empty);
            Vessels = new VesselSyncSystem(
                () => Players.LocalPlayerId,
                () => LocalPlayerName,
                id => Players.Get(id)?.Name ?? Loc.T("otro jugador", "another player"),
                () => Settings.DefaultAccess,
                (message, delivery) => Send(message, delivery));
            SharedControl = new SharedControlSystem(
                () => Players.LocalPlayerId,
                id => Vessels.Find(id) is TrackedVessel tracked && Vessels.IsPilotedByMe(tracked),
                () => MultiplayerWindow != null && MultiplayerWindow.ChatFocused,
                (message, delivery) => Send(message, delivery));
            Progress = new ProgressSync(message => Send(message));
            Roster = new RosterSync(message => Send(message), name => Vessels.IsCrewOfRemoteVessel(name));
            ConnectWindow = new ConnectWindow(this);
            MultiplayerWindow = new MultiplayerWindow(this);
            ModsWindow = new ModsWindow(this);
            PublicServersWindow = new PublicServersWindow(this);
            HostingWindow = new HostingWindow(this);
            HandoverWindow = new HandoverWindow(this);
            _toolbar = new ToolbarButton(SetMultiplayerWindowVisible);
        }

        public ClientSettings Settings { get; }

        public SharedClock Clock { get; } = new SharedClock();

        public PlayerRegistry Players { get; } = new PlayerRegistry();

        public ChatLog Chat { get; } = new ChatLog();

        public TimeSyncSystem TimeSync { get; }

        public WarpSystem Warp { get; }

        public VesselSyncSystem Vessels { get; }

        public SharedControlSystem SharedControl { get; }

        public ProgressSync Progress { get; }

        public RosterSync Roster { get; }

        /// <summary>Tipo de partida del servidor (con el que se crea la partida local).</summary>
        public GameMode GameMode { get; private set; }

        public HandoverWindow HandoverWindow { get; }

        public ConnectWindow ConnectWindow { get; }

        public MultiplayerWindow MultiplayerWindow { get; }

        public ModsWindow ModsWindow { get; }

        public PublicServersWindow PublicServersWindow { get; }

        public HostingWindow HostingWindow { get; }

        /// <summary>Mods del servidor comparados con los de esta instalación (vacío hasta conectarse).</summary>
        public List<ModDifference> ModDifferences { get; private set; } = new List<ModDifference>();

        public string ModSummary { get; private set; } = string.Empty;

        /// <summary>Hay diferencias de mods y la partida espera a que el jugador decida si entra igualmente.</summary>
        public bool WaitingForModCheck { get; private set; }

        public SessionState State { get; private set; }

        public string StatusText { get; private set; } = string.Empty;

        public string ServerName { get; private set; } = string.Empty;

        /// <summary>Código del servidor tal como lo dijo él (sin guion; vacío si no lo tiene). Se muestra con <see cref="ServerCode.Format"/>.</summary>
        public string ServerJoinCode { get; private set; } = string.Empty;

        /// <summary>Los puertos del servidor son alcanzables desde fuera, sin pasar por el relay.</summary>
        public bool ServerDirect { get; private set; }

        public double RoundTripMs => Clock.Sync.RoundTripTime * 1000.0;

        public bool UdpReady => _net.UdpReady;

        public string LocalPlayerName => Players.Get(Players.LocalPlayerId)?.Name ?? Settings.PlayerName;

        public void Start()
        {
            _toolbar.Register();
            Vessels.RegisterEvents();
        }

        /// <summary>
        /// <paramref name="server"/> es el texto del campo: un código del relay (K7M-Q2P) o la dirección del servidor.
        /// El puerto solo cuenta si es una dirección.
        /// </summary>
        public void Connect(string server, int port, string playerName, string password, string accountPassword)
        {
            if (State != SessionState.Disconnected)
                return;

            server = server?.Trim() ?? string.Empty;
            ConnectPlan plan;
            string status;
            if (ServerCode.LooksLikeCode(server))
            {
                // Un código mal escrito no se busca en el relay: el jugador lo corrige.
                if (!ServerCode.TryNormalize(server, out string code))
                {
                    StatusText = Loc.T("Ese código no es válido", "That code is not valid");
                    return;
                }

                server = ServerCode.Format(code);
                plan = ConnectPlan.ForCode(Settings.RelayAddress, code);
                status = Loc.T($"Buscando {server}...", $"Looking for {server}...");
            }
            else
            {
                Settings.Port = port;
                plan = ConnectPlan.Direct(server, port);
                status = Loc.T($"Conectando a {server}:{port}...", $"Connecting to {server}:{port}...");
            }

            Settings.Host = server;
            Settings.PlayerName = playerName;
            Settings.AccountPassword = accountPassword ?? string.Empty;
            Settings.Save();

            _password = password ?? string.Empty;
            ResetSession();
            Chat.Clear();
            State = SessionState.Connecting;
            StatusText = status;
            ClientLog.Info(StatusText);

            // La consulta al relay, el intento directo y la entrada por el relay corren en los hilos de NetClient.
            _net.Connect(plan);
        }

        public void Disconnect(string reason)
        {
            if (State == SessionState.Disconnected)
                return;

            _net.Disconnect(reason);
            OnDisconnected(reason);
        }

        public void Send(IMessage message, Delivery delivery = Delivery.Reliable)
        {
            if (State == SessionState.Joined)
                _net.Send(message, delivery);
        }

        public void SendChat(string text)
        {
            text = text?.Trim();
            if (!string.IsNullOrEmpty(text))
                Send(new ChatMessage { Text = text });
        }

        public void SetStatus(string text) => StatusText = text;

        public void SetMultiplayerWindowVisible(bool visible)
        {
            MultiplayerWindow.Visible = visible;
            if (visible)
                Chat.Unread = 0;
            _toolbar.SetOn(visible);
        }

        public void Update()
        {
            IdleTracker.Poll();
            PumpNetwork();
            if (State != SessionState.Joined)
                return;

            if (_startGamePending && Clock.HasState && !WaitingForModCheck && HighLogic.LoadedScene == GameScenes.MAINMENU)
                StartGame();

            if (ClientScenes.IsGameplay)
            {
                _enteredGame = true;
            }
            else if (_enteredGame && HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                Disconnect(Loc.T("Salió al menú principal", "Left to the main menu"));
                return;
            }

            double now = LocalClock.Now;
            if (now >= _nextPing)
            {
                _nextPing = now + ProtocolInfo.PingIntervalSeconds;
                _net.Send(new PingMessage { ClientTime = now });
            }

            TimeSync.Update();
            Warp.Update();
            _status.Update(message => Send(message), Vessels.SpectatingId != Guid.Empty);
            Vessels.Update();
            SharedControl.Update();
            Progress.Update();
            Roster.Update();
        }

        public void OnGUI()
        {
            UiVisibility.Ensure();
            if (!UiVisibility.Visible)
                return;

            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                ConnectWindow.Draw();
                PublicServersWindow.Draw();
                ModsWindow.Draw();
                HostingWindow.Draw();
                return;
            }

            if (State != SessionState.Joined || !ClientScenes.IsGameplay)
                return;

            RemoteVesselLabels.Draw(this);
            SpectateBanner.Draw(this);
            MultiplayerWindow.Draw();
            ModsWindow.Draw();
            HandoverWindow.Draw();
        }

        public void Shutdown(string reason)
        {
            if (_shutDown)
                return;

            _shutDown = true;
            _toolbar.Unregister();
            Vessels.UnregisterEvents();
            if (State != SessionState.Disconnected)
                _net.Disconnect(reason);
        }

        private void StartGame()
        {
            _startGamePending = false;
            ConnectWindow.Visible = false;
            PublicServersWindow.Visible = false;
            try
            {
                GameSession.StartGame(Clock.EstimateUniversalTime(LocalClock.Now), GameMode);
            }
            catch (Exception ex)
            {
                ClientLog.Error($"No se pudo iniciar la partida: {ex}");
                Disconnect($"No se pudo iniciar la partida: {ex.Message}");
            }
        }

        private void PumpNetwork()
        {
            while (_net.TryDequeue(out NetEvent netEvent))
            {
                switch (netEvent.Type)
                {
                    case NetEventType.Connected:
                        OnConnected();
                        break;
                    case NetEventType.Disconnected:
                        OnDisconnected(netEvent.Reason);
                        break;
                    case NetEventType.Message:
                        try
                        {
                            HandleMessage(netEvent.Message);
                        }
                        catch (Exception ex)
                        {
                            ClientLog.Error($"Error procesando {netEvent.Message.Type}: {ex}");
                        }

                        break;
                }
            }
        }

        private void OnConnected()
        {
            if (State != SessionState.Connecting)
                return;

            State = SessionState.Handshaking;
            StatusText = Loc.T("Conectado. Identificándose...", "Connected. Signing in...");
            _net.Send(new HandshakeRequestMessage
            {
                ProtocolVersion = ProtocolInfo.Version,
                PlayerName = Settings.PlayerName,
                Password = _password,
                AccountPassword = Settings.AccountPassword,
                ModVersion = ModVersion,
                GameVersion = Versioning.VersionString,
                Mods = ModScanner.Installed,
            });
        }

        private void HandleMessage(IMessage message)
        {
            switch (message)
            {
                case HandshakeResponseMessage response:
                    OnHandshakeResponse(response);
                    break;
                case PongMessage pong:
                    Clock.AddPong(pong, LocalClock.Now);
                    break;
                case TimeStateMessage time:
                    Clock.ApplyState(time, LocalClock.Now);
                    break;
                case ChatMessage chat:
                    OnChat(chat);
                    break;
                case ServerCodeMessage serverCode:
                    OnServerCode(serverCode);
                    break;
                case PlayerListMessage _:
                case PlayerJoinedMessage _:
                case PlayerLeftMessage _:
                case PlayerStatusMessage _:
                    Players.Handle(message);
                    break;
                case VesselProtoMessage _:
                case VesselUpdateMessage _:
                case VesselRemoveMessage _:
                case VesselControlMessage _:
                case VesselOwnerMessage _:
                case VesselRejectMessage _:
                    Vessels.Handle(message);
                    break;
                case ProgressStateMessage _:
                case ScienceSubjectsMessage _:
                case ContractsMessage _:
                    Progress.Handle(message);
                    break;
                case KerbalsMessage kerbals:
                    Roster.Handle(kerbals);
                    break;
                case VesselCopilotMessage _:
                case VesselInputMessage _:
                case VesselActionMessage _:
                    SharedControl.Handle(message);
                    break;
                case VesselHandoverAskMessage ask:
                    HandoverWindow.Ask(ask);
                    break;
            }
        }

        private void OnHandshakeResponse(HandshakeResponseMessage response)
        {
            CompareMods(response);
            if (!response.Accepted)
            {
                // El servidor cierra justo después; el motivo llega también con la desconexión.
                StatusText = response.RejectReason;
                return;
            }

            State = SessionState.Joined;
            Players.LocalPlayerId = response.PlayerId;
            ServerName = response.ServerName ?? string.Empty;
            GameMode = GameModes.IsValid(response.GameMode) ? response.GameMode : GameMode.Sandbox;
            Progress.Start(GameMode);
            Roster.Start();
            StatusText = _net.ViaRelay
                ? Loc.T($"Conectado a {ServerName} (por el relay)", $"Connected to {ServerName} (through the relay)")
                : Loc.T($"Conectado a {ServerName} (directo)", $"Connected to {ServerName} (direct)");
            _nextPing = 0;
            _startGamePending = true;
            _net.StartUdp(response.UdpToken);
            _toolbar.SetAvailable(true);

            if (!string.IsNullOrEmpty(response.Motd))
                Chat.Add(ServerName, response.Motd, SystemColor, true);
            if (response.NewAccount)
                Chat.Add("MultiKerbal", Loc.T(
                    $"Cuenta creada: el nombre {Settings.PlayerName} es tuyo con esta contraseña. No la olvides: el administrador solo puede borrar la cuenta, no recuperarla.",
                    $"Account created: the name {Settings.PlayerName} is yours with this password. Don't forget it: the admin can only delete the account, not recover it."), SystemColor, true);
            if (GameMode != GameMode.Sandbox)
                Chat.Add("MultiKerbal", Loc.T(
                    $"Partida de {GameModes.Describe(GameMode)}: el progreso es de todos.",
                    $"{GameModes.Describe(GameMode)} game: progress is shared by everyone."), SystemColor, true);
            ClientLog.Info($"Unido a \"{ServerName}\" como {Settings.PlayerName} (jugador {response.PlayerId})");
        }

        /// <summary>El servidor dice su código al entrar y otra vez si cambia. Viene de fuera: solo vale si es un código bien formado.</summary>
        private void OnServerCode(ServerCodeMessage message)
        {
            if (ServerCode.TryNormalize(message.Code, out string code))
            {
                ServerJoinCode = code;
                ServerDirect = message.Direct;
            }
            else
            {
                ServerJoinCode = string.Empty;
                ServerDirect = false;
            }
        }

        /// <summary>
        /// Compara los mods con los del servidor. Si algo no coincide se abre la ventana y la partida espera:
        /// mejor verlo antes de entrar que descubrirlo cuando una nave no se carga.
        /// </summary>
        private void CompareMods(HandshakeResponseMessage response)
        {
            ModDifferences = ModCompare.Compare(response.Mods, ModScanner.Installed);
            ModSummary = ModDifferences.Count == 0
                ? Loc.T("El servidor todavía no tiene lista de mods.", "The server has no mod list yet.")
                : Loc.T("Respecto al servidor: ", "Compared with the server: ") + ModCompare.Summarize(ModDifferences);

            bool problems = ModDifferences.Exists(d => d.IsProblem);
            WaitingForModCheck = problems && response.Accepted;
            if (!problems)
                return;

            ClientLog.Warn($"Mods: {ModSummary}");
            ModsWindow.Visible = true;
        }

        /// <summary>El jugador decide entrar aunque los mods no coincidan.</summary>
        public void AcceptMods()
        {
            WaitingForModCheck = false;
            Chat.Add("MultiKerbal", ModSummary, SystemColor, true);
        }

        private void OnChat(ChatMessage chat)
        {
            bool isSystem = chat.SenderId == ChatMessage.SystemSenderId;
            PlayerInfo sender = Players.Get(chat.SenderId);
            string name = isSystem ? ServerName : sender?.Name ?? $"#{chat.SenderId}";
            Chat.Add(name, chat.Text, isSystem ? SystemColor : PlayerRegistry.ColorOf(sender), isSystem);

            if (!MultiplayerWindow.Visible && ClientScenes.IsGameplay)
            {
                Chat.Unread++;
                // Los mensajes en pantalla admiten etiquetas de texto enriquecido: neutralizarlas.
                string text = (isSystem ? chat.Text : $"{name}: {chat.Text}").Replace("<", "‹");
                ScreenMessages.PostScreenMessage(text, 6f, ScreenMessageStyle.UPPER_LEFT);
            }
        }

        private void OnDisconnected(string reason)
        {
            if (State == SessionState.Disconnected)
                return;

            bool leaveGame = _enteredGame || (State == SessionState.Joined && ClientScenes.IsGameplay);
            State = SessionState.Disconnected;
            StatusText = string.IsNullOrEmpty(reason) ? Loc.T("Desconectado", "Disconnected") : reason;
            ClientLog.Info($"Desconectado: {StatusText}");

            Chat.Add("MultiKerbal", Loc.T($"Desconectado: {StatusText}", $"Disconnected: {StatusText}"), SystemColor, true);
            ResetSession();
            _toolbar.SetAvailable(false);
            MultiplayerWindow.Visible = false;
            ConnectWindow.Visible = true;

            // La partida local no es la autoridad: sin servidor no se puede seguir jugando en ella.
            if (leaveGame)
                GameSession.ReturnToMainMenu();
        }

        private void ResetSession()
        {
            Clock.Reset();
            Players.Clear();
            Warp.Reset();
            Vessels.Reset();
            SharedControl.Reset();
            Progress.Reset();
            Roster.Reset();
            HandoverWindow.Clear();
            TimeSync.RequestHardSync();
            _status.Reset();
            WaitingForModCheck = false;
            _startGamePending = false;
            _enteredGame = false;
            ServerName = string.Empty;
            ServerJoinCode = string.Empty;
            ServerDirect = false;
        }
    }
}

// Hola, si trabajas en esto y eres un humano, ten de antemano de que no tengo ni idea de que esta haciendo Claude en el codigo, gracias.

// Aunque no se preocupen, estoy aprendiendo C++ y Python, y despues voy a empezar a aprender C#.

/* 

1234

*/