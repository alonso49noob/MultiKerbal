using MultiKerbal.Client.Systems;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Time;
using MultiKerbal.Common.Vessels;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Ventana en partida: estado del reloj, jugadores, naves y chat. Cada sección se puede plegar.</summary>
    internal sealed class MultiplayerWindow
    {
        private const int WindowId = 0x4D4B0002;
        private const float Width = 500f;
        private const float VesselListHeight = 150f;
        private const float ChatHeight = 190f;
        private const string InputControl = "MultiKerbalChatInput";
        private const string InputLockId = "MultiKerbalChat";

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private readonly WarpSettingsWindow _warpSettings;
        private readonly LabelSettingsWindow _labelSettings;
        private readonly VesselOwnershipWindow _ownership;
        private Rect _rect = new Rect(60f, 140f, Width, 0f);
        private Vector2 _chatScroll;
        private Vector2 _vesselScroll;
        private int _seenChatVersion = -1;
        private string _input = string.Empty;
        private bool _inputFocused;
        private bool _locked;
        private bool _visible;
        private bool _showPlayers = true;
        private bool _showVessels = true;
        private bool _showChat = true;

        public MultiplayerWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
            _warpSettings = new WarpSettingsWindow(core);
            _labelSettings = new LabelSettingsWindow(core);
            _ownership = new VesselOwnershipWindow(core);
        }

        /// <summary>El foco está en el cuadro del chat: las teclas son texto, no mandos.</summary>
        public bool ChatFocused => _inputFocused;

        public bool Visible
        {
            get => _visible;
            set
            {
                _visible = value;
                if (!value)
                {
                    _inputFocused = false;
                    _warpSettings.Visible = false;
                    _labelSettings.Visible = false;
                    _ownership.Visible = false;
                    UpdateInputLock();
                }
            }
        }

        public void Draw()
        {
            if (!_visible)
                return;

            UiStyles.Apply();

            // Clic fuera de la ventana: soltar el foco del chat para devolver los controles a KSP.
            Event current = Event.current;
            if (_inputFocused && current.type == EventType.MouseDown && !_rect.Contains(current.mousePosition))
            {
                GUI.FocusControl(null);
                _inputFocused = false;
            }

            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "MultiKerbal — " + _core.ServerName, GUILayout.Width(Width));
            UpdateInputLock();
            _warpSettings.Draw();
            _labelSettings.Draw();
            _ownership.Draw();
        }

        /// <summary>Cabecera de sección plegable: devuelve si hay que dibujar su contenido.</summary>
        private static bool Section(ref bool open, string title, string extra = null)
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(open ? UiStyles.Open : UiStyles.Closed, UiStyles.Label, GUILayout.Width(22f)))
                open = !open;
            if (GUILayout.Button(title, UiStyles.Bold, GUILayout.Width(180f)))
                open = !open;
            if (!string.IsNullOrEmpty(extra))
                GUILayout.Label(extra, UiStyles.Muted);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return open;
        }

        private void DrawContents(int id)
        {
            DrawStatus();
            DrawPlayers();
            DrawVessels();
            DrawChat();
            DrawButtons();
            GUI.DragWindow();
        }

        private void DrawStatus()
        {
            SharedClock clock = _core.Clock;
            string warp = clock.Rate > 1.0
                ? Loc.T($"warp x{clock.Rate:0.##}", $"warp x{clock.Rate:0.##}")
                : Loc.T("tiempo normal", "normal time");
            GUILayout.Label($"{KerbalTime.Format(Planetarium.GetUniversalTime())} · {warp}", UiStyles.Label);

            GUILayout.BeginHorizontal();
            UiStyles.ColoredLabel($"ping {_core.RoundTripMs:0} ms", PingColor(_core.RoundTripMs), GUILayout.Width(110f));
            if (!_core.UdpReady)
                UiStyles.ColoredLabel(Loc.T("sin UDP (va todo por TCP)", "no UDP (all over TCP)"), UiStyles.Warning);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static Color PingColor(double milliseconds) =>
            milliseconds < 120 ? UiStyles.Good : milliseconds < 300 ? UiStyles.Warning : UiStyles.Bad;

        private void DrawPlayers()
        {
            if (!Section(ref _showPlayers, Loc.T("Jugadores", "Players"), $"{_core.Players.Count}"))
                return;

            foreach (PlayerInfo player in _core.Players.All)
            {
                GUILayout.BeginHorizontal();
                string name = player.Id == _core.Players.LocalPlayerId
                    ? player.Name + Loc.T(" (tú)", " (you)")
                    : player.Name;
                UiStyles.ColoredLabel(name, PlayerRegistry.ColorOf(player), GUILayout.Width(160f));
                GUILayout.Label(DescribeActivity(player), UiStyles.Muted);
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>Naves del universo: dueño y acceso, quién la pilota y, en vuelo, a qué distancia están.</summary>
        private void DrawVessels()
        {
            int total = _core.Vessels.TotalVessels;
            if (!Section(ref _showVessels, Loc.T("Naves", "Vessels"), $"{total}"))
                return;

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Tus naves nuevas:", "Your new vessels:"), UiStyles.Muted, GUILayout.Width(150f));
            if (GUILayout.Button(UiStrings.AccessPlural(_core.Settings.DefaultAccess), GUILayout.Width(130f)))
            {
                _core.Settings.DefaultAccess = NextAccess(_core.Settings.DefaultAccess);
                _core.Settings.Save();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (total == 0)
            {
                GUILayout.Label(Loc.T("Ninguna todavía", "None yet"), UiStyles.Muted);
                return;
            }

            Vessel active = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            _vesselScroll = GUILayout.BeginScrollView(_vesselScroll, GUILayout.Height(VesselListHeight));
            foreach (Vessels.TrackedVessel tracked in _core.Vessels.Tracked)
            {
                GUILayout.BeginHorizontal();
                UiStyles.ColoredLabel(
                    string.IsNullOrEmpty(tracked.Name) ? Loc.T("(sin nombre)", "(unnamed)") : tracked.Name,
                    _core.Players.ColorOfOwner(tracked.OwnerName),
                    GUILayout.Width(150f));
                GUILayout.Label(DescribeOwner(tracked), UiStyles.Muted, GUILayout.Width(125f));
                GUILayout.Label(DescribePilot(tracked), UiStyles.Muted, GUILayout.Width(85f));
                GUILayout.Label(DescribeDistance(tracked, active), UiStyles.Muted);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("Nave", "Vessel"), GUILayout.Width(62f)))
                    _ownership.Toggle(tracked.Id);
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
        }

        private static VesselAccess NextAccess(VesselAccess access)
        {
            switch (access)
            {
                case VesselAccess.Private:
                    return VesselAccess.Shared;
                case VesselAccess.Shared:
                    return VesselAccess.Public;
                default:
                    return VesselAccess.Private;
            }
        }

        private string DescribeOwner(Vessels.TrackedVessel tracked)
        {
            if (!VesselPermissions.HasOwner(tracked.OwnerName))
                return Loc.T("sin dueño", "no owner");

            string owner = _core.Vessels.IsOwnedByMe(tracked) ? Loc.T("tuya", "yours") : tracked.OwnerName;
            return $"{owner} · {UiStrings.Access(tracked.Access)}";
        }

        private string DescribePilot(Vessels.TrackedVessel tracked)
        {
            if (_core.Vessels.IsPilotedByMe(tracked))
                return Loc.T("la pilotas", "you fly it");

            PlayerInfo pilot = _core.Players.Get(tracked.ControllerId);
            return pilot != null ? Loc.T($"pilota {pilot.Name}", $"{pilot.Name} flies it") : string.Empty;
        }

        private void DrawChat()
        {
            int unread = _core.Chat.Unread;
            if (!Section(ref _showChat, "Chat", unread > 0 ? Loc.T($"{unread} sin leer", $"{unread} unread") : null))
                return;

            if (_core.Chat.Version != _seenChatVersion)
            {
                _seenChatVersion = _core.Chat.Version;
                _chatScroll.y = float.MaxValue;
            }

            _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(ChatHeight));
            foreach (ChatLog.Line line in _core.Chat.Lines)
                UiStyles.ColoredLabel(line.IsSystem ? line.Text : $"{line.Sender}: {line.Text}", line.Color);
            GUILayout.EndScrollView();
            _core.Chat.Unread = 0;

            Event current = Event.current;
            bool submit = current.type == EventType.KeyDown
                          && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                          && GUI.GetNameOfFocusedControl() == InputControl;

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(InputControl);
            _input = GUILayout.TextField(_input, ProtocolInfo.MaxChatLength);
            bool clicked = GUILayout.Button(Loc.T("Enviar", "Send"), GUILayout.Width(80f));
            GUILayout.EndHorizontal();

            if (submit || clicked)
            {
                _core.SendChat(_input);
                _input = string.Empty;
                if (submit)
                    current.Use();
            }

            _inputFocused = GUI.GetNameOfFocusedControl() == InputControl;
        }

        private void DrawButtons()
        {
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Warp"))
                _warpSettings.Visible = !_warpSettings.Visible;
            if (GUILayout.Button(Loc.T("Etiquetas", "Labels")))
                _labelSettings.Visible = !_labelSettings.Visible;
            if (GUILayout.Button("Mods"))
                _core.ModsWindow.Visible = !_core.ModsWindow.Visible;
            if (GUILayout.Button(Loc.T("Salir", "Leave")))
                _core.Disconnect(Loc.T("Desconectado por el jugador", "Disconnected by the player"));
            if (GUILayout.Button(Loc.T("Cerrar", "Close"), GUILayout.Width(80f)))
                _core.SetMultiplayerWindowVisible(false);
            GUILayout.EndHorizontal();
        }

        private string DescribeDistance(Vessels.TrackedVessel tracked, Vessel active)
        {
            if (active == null)
                return string.Empty;

            Vessel vessel = _core.Vessels.VesselOf(tracked);
            if (vessel == null || vessel == active)
                return string.Empty;

            double distance = (vessel.transform.position - active.transform.position).magnitude;
            return distance < 1000.0 ? $"{distance:0} m" : $"{distance / 1000.0:0.#} km";
        }

        /// <summary>Mientras se escribe, KSP no debe interpretar las teclas (espacio = separar etapa...).</summary>
        private void UpdateInputLock()
        {
            bool shouldLock = _visible && _inputFocused;
            if (shouldLock == _locked)
                return;

            _locked = shouldLock;
            if (shouldLock)
                InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, InputLockId);
            else
                InputLockManager.RemoveControlLock(InputLockId);
        }

        private static string DescribeActivity(PlayerInfo player)
        {
            switch (player.Activity)
            {
                case PlayerActivity.MainMenu:
                    return Loc.T("En el menú", "In the menu");
                case PlayerActivity.SpaceCenter:
                    return Loc.T("En el Centro Espacial", "At the Space Center");
                case PlayerActivity.Editor:
                    return Loc.T("Construyendo", "Building");
                case PlayerActivity.Flight:
                    return string.IsNullOrEmpty(player.Detail)
                        ? Loc.T("En vuelo", "In flight")
                        : Loc.T("Pilotando " + player.Detail, "Flying " + player.Detail);
                case PlayerActivity.TrackingStation:
                    return Loc.T("En la estación de seguimiento", "At the tracking station");
                default:
                    return Loc.T("Cargando...", "Loading...");
            }
        }
    }
}
