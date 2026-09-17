using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Qué etiquetas mostrar sobre las naves de otros jugadores. Los cambios se guardan al momento.</summary>
    internal sealed class LabelSettingsWindow
    {
        private const int WindowId = 0x4D4B0004;
        private const float Width = 420f;
        private const float ColumnWidth = 190f;
        private const int Columns = 2;

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(540f, 160f, Width, 0f);

        public LabelSettingsWindow(ClientCore core)
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
            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "Etiquetas de naves", GUILayout.Width(Width));
        }

        private void DrawContents(int id)
        {
            LabelFilter filter = _core.Settings.Labels;
            bool changed = false;

            bool enabled = GUILayout.Toggle(filter.Enabled, "Mostrar nombre y dueño sobre las naves de otros jugadores");
            if (enabled != filter.Enabled)
            {
                filter.Enabled = enabled;
                changed = true;
            }

            GUILayout.Space(6f);
            GUI.enabled = filter.Enabled;
            GUILayout.Label("Solo en estos tipos de nave:", UiStyles.Bold);

            LabelCategoryInfo[] categories = LabelFilter.Categories;
            for (int row = 0; row < categories.Length; row += Columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < Columns && row + column < categories.Length; column++)
                {
                    LabelCategoryInfo info = categories[row + column];
                    bool shown = GUILayout.Toggle(filter[info.Category], info.Text, GUILayout.Width(ColumnWidth));
                    if (shown != filter[info.Category])
                    {
                        filter[info.Category] = shown;
                        changed = true;
                    }
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Todos"))
                changed |= filter.SetAll(true);
            if (GUILayout.Button("Ninguno"))
                changed |= filter.SetAll(false);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            GUILayout.Space(6f);
            GUILayout.Label("El tipo es el que eligió el dueño al ponerle nombre a la nave.", UiStyles.Muted);
            GUILayout.Label($"En vuelo solo se etiquetan las naves a menos de {RemoteVesselLabels.MaxFlightDistance / 1000.0:0} km; en el mapa, todas.", UiStyles.Muted);

            if (GUILayout.Button("Cerrar"))
                Visible = false;

            if (changed)
                _core.Settings.Save();

            GUI.DragWindow();
        }
    }
}
