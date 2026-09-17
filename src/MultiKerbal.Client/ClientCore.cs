using System;
using MultiKerbal.Client.Systems;
using MultiKerbal.Client.UI;
using MultiKerbal.Client.Vessels;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
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
            TimeSync = new TimeSyncSystem(Clock);
            Warp = new WarpSystem(Clock, message => Send(message), () => LocalPlayerName, () => Settings.Warp);
            Vessels = new VesselSyncSystem(
                () => Players.LocalPlayerId,
                () => LocalPlayerName,
                id => Players.Get(id)?.Name ?? "otro jugador",
                () => Settings.DefaultAccess,
                (message, delivery) => Send(message, delivery));
            ConnectWindow = new ConnectWindow(this);
            MultiplayerWindow = new MultiplayerWindow(this);
            _toolbar = new ToolbarButton(SetMultiplayerWindowVisible);
        }

        public ClientSettings Settings { get; }

        public SharedClock Clock { get; } = new SharedClock();

        public PlayerRegistry Players { get; } = new PlayerRegistry();

        public ChatLog Chat { get; } = new ChatLog();

        public TimeSyncSystem TimeSync { get; }

        public WarpSystem Warp { get; }

        public VesselSyncSystem Vessels { get; }

        public ConnectWindow ConnectWindow { get; }

        public MultiplayerWindow MultiplayerWindow { get; }

        public SessionState State { get; private set; }

        public string StatusText { get; private set; } = string.Empty;

        public string ServerName { get; private set; } = string.Empty;

        public double RoundTripMs => Clock.Sync.RoundTripTime * 1000.0;

        public bool UdpReady => _net.UdpReady;

        public string LocalPlayerName => Players.Get(Players.LocalPlayerId)?.Name ?? Settings.PlayerName;

        public void Start()
        {
            _toolbar.Register();
            Vessels.RegisterEvents();
        }

        public void Connect(string host, int port, string playerName, string password)
        {
            if (State != SessionState.Disconnected)
                return;

            Settings.Host = host;
            Settings.Port = port;
            Settings.PlayerName = playerName;
            Settings.Save();

            _password = password ?? string.Empty;
            ResetSession();
            Chat.Clear();
            State = SessionState.Connecting;
            StatusText = $"Conectando a {host}:{port}...";
            ClientLog.Info(StatusText);
            _net.Connect(host, port);
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

            if (_startGamePending && Clock.HasState && HighLogic.LoadedScene == GameScenes.MAINMENU)
                StartGame();

            if (ClientScenes.IsGameplay)
            {
                _enteredGame = true;
            }
            else if (_enteredGame && HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                Disconnect("Salió al menú principal");
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
            _status.Update(message => Send(message));
            Vessels.Update();
        }

        public void OnGUI()
        {
            UiVisibility.Ensure();
            if (!UiVisibility.Visible)
                return;

            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                ConnectWindow.Draw();
                return;
            }

            if (State != SessionState.Joined || !ClientScenes.IsGameplay)
                return;

            RemoteVesselLabels.Draw(this);
            MultiplayerWindow.Draw();
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
            try
            {
                GameSession.StartSandbox(Clock.EstimateUniversalTime(LocalClock.Now));
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
            StatusText = "Conectado. Identificándose...";
            _net.Send(new HandshakeRequestMessage
            {
                ProtocolVersion = ProtocolInfo.Version,
                PlayerName = Settings.PlayerName,
                Password = _password,
                ModVersion = ModVersion,
                GameVersion = Versioning.VersionString,
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
                    Vessels.Handle(message);
                    break;
            }
        }

        private void OnHandshakeResponse(HandshakeResponseMessage response)
        {
            if (!response.Accepted)
            {
                // El servidor cierra justo después; el motivo llega también con la desconexión.
                StatusText = response.RejectReason;
                return;
            }

            State = SessionState.Joined;
            Players.LocalPlayerId = response.PlayerId;
            ServerName = response.ServerName ?? string.Empty;
            StatusText = $"Conectado a {ServerName}";
            _nextPing = 0;
            _startGamePending = true;
            _net.StartUdp(response.UdpToken);
            _toolbar.SetAvailable(true);

            if (!string.IsNullOrEmpty(response.Motd))
                Chat.Add(ServerName, response.Motd, SystemColor, true);
            ClientLog.Info($"Unido a \"{ServerName}\" como {Settings.PlayerName} (jugador {response.PlayerId})");
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
            StatusText = string.IsNullOrEmpty(reason) ? "Desconectado" : reason;
            ClientLog.Info($"Desconectado: {StatusText}");

            Chat.Add("MultiKerbal", $"Desconectado: {StatusText}", SystemColor, true);
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
            TimeSync.RequestHardSync();
            _status.Reset();
            _startGamePending = false;
            _enteredGame = false;
            ServerName = string.Empty;
        }
    }
}

// Hola, si trabajas en esto y eres un humano, ten de antemano de que no tengo ni idea de que esta haciendo Claude en el codigo, gracias.

// Aunque no se preocupen, estoy aprendiendo C++ y Python, y despues voy a empezar a aprender C#.

/* 

1234

*/