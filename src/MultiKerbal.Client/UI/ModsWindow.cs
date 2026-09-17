using System.Collections.Generic;
using MultiKerbal.Common.Mods;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// Los mods del servidor comparados con los instalados aquí: verde si está, rojo si falta.
    /// Se abre sola al conectarse cuando algo no coincide, antes de entrar en la partida.
    /// </summary>
    internal sealed class ModsWindow
    {
        private const int WindowId = 0x4D4B0006;
        private const float Width = 520f;

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(80f, 80f, Width, 0f);
        private Vector2 _scroll;
        private bool _onlyProblems = true;

        public ModsWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
        }

        public bool Visible { get; set; }

        public void Draw()
        {
            if (!Visible)
                return;

            UiStyles.Apply();
            _rect = GUILayout.Window(WindowId, _rect, _drawContents, Loc.T("Mods del servidor", "Server mods"), GUILayout.Width(Width));
        }

        private void DrawContents(int id)
        {
            List<ModDifference> differences = _core.ModDifferences;
            bool waiting = _core.WaitingForModCheck;

            if (differences.Count == 0)
            {
                GUILayout.Label(
                    _core.State == SessionState.Disconnected
                        ? Loc.T("Conéctate a un servidor para comparar sus mods con los tuyos.",
                                "Connect to a server to compare its mods with yours.")
                        : Loc.T("El servidor aún no tiene lista de mods: la fija el primer jugador que entra.",
                                "The server has no mod list yet: the first player to join sets it."),
                    UiStyles.Label);
            }
            else
            {
                GUILayout.Label(_core.ModSummary, UiStyles.Label);
                _onlyProblems = GUILayout.Toggle(_onlyProblems, Loc.T("Ver solo los que no coinciden", "Show only what does not match"));
                DrawList(differences);
            }

            GUILayout.BeginHorizontal();
            if (waiting && GUILayout.Button(Loc.T("Entrar igualmente", "Join anyway")))
            {
                _core.AcceptMods();
                Visible = false;
            }

            if (waiting && GUILayout.Button(Loc.T("Desconectar", "Disconnect")))
            {
                _core.Disconnect(Loc.T("Desconectado por diferencias de mods", "Disconnected over mod differences"));
                Visible = false;
            }

            if (GUILayout.Button(Loc.T("Cerrar", "Close"), GUILayout.Width(100f)))
                Visible = false;
            GUILayout.EndHorizontal();

            if (waiting)
            {
                GUILayout.Label(
                    Loc.T("La partida no empieza hasta que decidas. Las naves con piezas que te falten no se cargarán.",
                          "The game will not start until you decide. Vessels using parts you lack will not load."),
                    UiStyles.Muted);
            }

            GUI.DragWindow();
        }

        private void DrawList(List<ModDifference> differences)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(320f));
            int hidden = 0;
            foreach (ModDifference difference in differences)
            {
                if (_onlyProblems && !difference.IsProblem)
                {
                    hidden++;
                    continue;
                }

                GUILayout.BeginHorizontal();
                UiStyles.ColoredLabel(Mark(difference.Status), ColorOf(difference.Status), GUILayout.Width(30f));
                GUILayout.Label(difference.Name, UiStyles.Label, GUILayout.Width(220f));
                GUILayout.Label(Describe(difference), UiStyles.Muted);
                GUILayout.EndHorizontal();
            }

            if (hidden > 0)
            {
                GUILayout.Label(
                    Loc.T($"… y {hidden} que ya tienes igual que el servidor", $"… and {hidden} you already have, same as the server"),
                    UiStyles.Muted);
            }

            GUILayout.EndScrollView();
        }

        private static string Mark(ModStatus status)
        {
            switch (status)
            {
                case ModStatus.Ok:
                    return UiStyles.Tick;
                case ModStatus.Missing:
                    return UiStyles.Cross;
                case ModStatus.OtherVersion:
                    return "!";
                default:
                    return "+";
            }
        }

        private static Color ColorOf(ModStatus status)
        {
            switch (status)
            {
                case ModStatus.Ok:
                    return UiStyles.Good;
                case ModStatus.Missing:
                    return UiStyles.Bad;
                case ModStatus.OtherVersion:
                    return UiStyles.Warning;
                default:
                    return Color.gray;
            }
        }

        private static string Describe(ModDifference difference)
        {
            switch (difference.Status)
            {
                case ModStatus.Missing:
                    return difference.ServerVersion.Length > 0
                        ? Loc.T($"te falta (servidor: {difference.ServerVersion})", $"missing (server: {difference.ServerVersion})")
                        : Loc.T("te falta", "missing");
                case ModStatus.OtherVersion:
                    return Loc.T(
                        $"otra versión — servidor: {difference.ServerVersion}, tú: {difference.PlayerVersion}",
                        $"different version — server: {difference.ServerVersion}, you: {difference.PlayerVersion}");
                case ModStatus.Extra:
                    return difference.PlayerVersion.Length > 0
                        ? Loc.T($"solo lo tienes tú ({difference.PlayerVersion})", $"only you have it ({difference.PlayerVersion})")
                        : Loc.T("solo lo tienes tú", "only you have it");
                default:
                    return difference.PlayerVersion.Length > 0 ? difference.PlayerVersion : Loc.T("instalado", "installed");
            }
        }
    }
}
