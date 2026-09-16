using MultiKerbal.Client.Systems;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Time;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Ventana en partida: estado del reloj, jugadores, naves y chat.</summary>
    internal sealed class MultiplayerWindow
    {
        private const int WindowId = 0x4D4B0002;
        private const float Width = 440f;
        private const int MaxVesselRows = 8;
        private const string InputControl = "MultiKerbalChatInput";
        private const string InputLockId = "MultiKerbalChat";

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private readonly WarpSettingsWindow _warpSettings;
        private Rect _rect = new Rect(60f, 140f, Width, 0f);
        private Vector2 _chatScroll;
        private int _seenChatVersion = -1;
        private string _input = string.Empty;
        private bool _inputFocused;
        private bool _locked;
        private bool _visible;

        public MultiplayerWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
            _warpSettings = new WarpSettingsWindow(core);
        }

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
        }

        private void DrawContents(int id)
        {
            DrawStatus();
            DrawPlayers();
            DrawVessels();
            DrawChat();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Ajustes de warp"))
                _warpSettings.Visible = !_warpSettings.Visible;
            if (GUILayout.Button("Desconectar"))
                _core.Disconnect("Desconectado por el jugador");
            if (GUILayout.Button("Cerrar"))
                _core.SetMultiplayerWindowVisible(false);
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private void DrawStatus()
        {
            SharedClock clock = _core.Clock;
            string warp = clock.Rate > 1.0 ? $"warp x{clock.Rate:0.##}" : "tiempo normal";
            string network = $"ping {_core.RoundTripMs:0} ms" + (_core.UdpReady ? string.Empty : " · sin UDP");
            GUILayout.Label($"{KerbalTime.Format(Planetarium.GetUniversalTime())} · {warp} · {network}", UiStyles.Muted);
        }

        private void DrawPlayers()
        {
            GUILayout.Label($"Jugadores ({_core.Players.Count})", UiStyles.Bold);
            foreach (PlayerInfo player in _core.Players.All)
            {
                GUILayout.BeginHorizontal();
                string name = player.Id == _core.Players.LocalPlayerId ? player.Name + " (tú)" : player.Name;
                UiStyles.ColoredLabel(name, PlayerRegistry.ColorOf(player), GUILayout.Width(160f));
                GUILayout.Label(DescribeActivity(player), UiStyles.Muted);
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>Naves del universo con su dueño; en vuelo, además, a qué distancia están.</summary>
        private void DrawVessels()
        {
            int total = _core.Vessels.TotalVessels;
            GUILayout.Label($"Naves ({total}, tuyas: {_core.Vessels.OwnVessels})", UiStyles.Bold);
            if (total == 0)
            {
                GUILayout.Label("Ninguna todavía", UiStyles.Muted);
                return;
            }

            Vessel active = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            int shown = 0;
            foreach (Vessels.TrackedVessel tracked in _core.Vessels.Tracked)
            {
                if (shown >= MaxVesselRows)
                {
                    GUILayout.Label($"… y {total - shown} más", UiStyles.Muted);
                    break;
                }

                shown++;
                PlayerInfo owner = _core.Players.Get(tracked.OwnerId);
                string ownerName = _core.Vessels.IsMine(tracked) ? "tuya" : owner?.Name ?? "sin dueño";
                GUILayout.BeginHorizontal();
                UiStyles.ColoredLabel(
                    string.IsNullOrEmpty(tracked.Name) ? "(sin nombre)" : tracked.Name,
                    tracked.OwnerId == 0 ? Color.gray : PlayerRegistry.ColorOf(owner),
                    GUILayout.Width(180f));
                GUILayout.Label(ownerName, UiStyles.Muted, GUILayout.Width(110f));
                GUILayout.Label(DescribeDistance(tracked, active), UiStyles.Muted);
                GUILayout.EndHorizontal();
            }
        }

        private void DrawChat()
        {
            GUILayout.Label("Chat", UiStyles.Bold);

            if (_core.Chat.Version != _seenChatVersion)
            {
                _seenChatVersion = _core.Chat.Version;
                _chatScroll.y = float.MaxValue;
            }

            _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(200f));
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
            bool clicked = GUILayout.Button("Enviar", GUILayout.Width(80f));
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
                    return "En el menú";
                case PlayerActivity.SpaceCenter:
                    return "En el Centro Espacial";
                case PlayerActivity.Editor:
                    return "Construyendo";
                case PlayerActivity.Flight:
                    return string.IsNullOrEmpty(player.Detail) ? "En vuelo" : "Pilotando " + player.Detail;
                case PlayerActivity.TrackingStation:
                    return "En la estación de seguimiento";
                default:
                    return "Cargando...";
            }
        }
    }
}
