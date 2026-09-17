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

        private static readonly Color Good = new Color(0.45f, 0.9f, 0.45f);
        private static readonly Color Warning = new Color(1f, 0.82f, 0.35f);
        private static readonly Color Bad = new Color(1f, 0.45f, 0.45f);

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
            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "Mods del servidor", GUILayout.Width(Width));
        }

        private void DrawContents(int id)
        {
            List<ModDifference> differences = _core.ModDifferences;
            bool waiting = _core.WaitingForModCheck;

            if (differences.Count == 0)
            {
                GUILayout.Label(
                    _core.State == SessionState.Disconnected
                        ? "Conéctate a un servidor para comparar sus mods con los tuyos."
                        : "El servidor aún no tiene lista de mods: la fija el primer jugador que entra.",
                    UiStyles.Label);
            }
            else
            {
                GUILayout.Label(_core.ModSummary, UiStyles.Label);
                _onlyProblems = GUILayout.Toggle(_onlyProblems, "Ver solo los que no coinciden");
                DrawList(differences);
            }

            GUILayout.BeginHorizontal();
            if (waiting && GUILayout.Button("Entrar igualmente"))
            {
                _core.AcceptMods();
                Visible = false;
            }

            if (waiting && GUILayout.Button("Desconectar"))
            {
                _core.Disconnect("Desconectado por diferencias de mods");
                Visible = false;
            }

            if (GUILayout.Button("Cerrar", GUILayout.Width(100f)))
                Visible = false;
            GUILayout.EndHorizontal();

            if (waiting)
                GUILayout.Label("La partida no empieza hasta que decidas. Las naves con piezas que te falten no se cargarán.", UiStyles.Muted);

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
                GUILayout.Label($"… y {hidden} que ya tienes igual que el servidor", UiStyles.Muted);
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
                    return Good;
                case ModStatus.Missing:
                    return Bad;
                case ModStatus.OtherVersion:
                    return Warning;
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
                        ? $"te falta (servidor: {difference.ServerVersion})"
                        : "te falta";
                case ModStatus.OtherVersion:
                    return $"otra versión — servidor: {difference.ServerVersion}, tú: {difference.PlayerVersion}";
                case ModStatus.Extra:
                    return difference.PlayerVersion.Length > 0
                        ? $"solo lo tienes tú ({difference.PlayerVersion})"
                        : "solo lo tienes tú";
                default:
                    return difference.PlayerVersion.Length > 0 ? difference.PlayerVersion : "instalado";
            }
        }
    }
}
