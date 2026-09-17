using UnityEngine;

namespace MultiKerbal.Client.UI
{
    internal static class UiStyles
    {
        private static GUISkin _skin;

        /// <summary>Sin texto enriquecido: los nombres y el chat vienen de otros jugadores.</summary>
        public static GUIStyle Label { get; private set; }

        public static GUIStyle Bold { get; private set; }

        public static GUIStyle Muted { get; private set; }

        /// <summary>Marcas de sí/no y flechas de plegar. La fuente de KSP no siempre las trae: si faltan, se usa texto.</summary>
        public static string Tick { get; private set; } = "OK";

        public static string Cross { get; private set; } = "X";

        public static string Open { get; private set; } = "-";

        public static string Closed { get; private set; } = "+";

        public static readonly Color Good = new Color(0.45f, 0.9f, 0.45f);

        public static readonly Color Warning = new Color(1f, 0.82f, 0.35f);

        public static readonly Color Bad = new Color(1f, 0.45f, 0.45f);

        /// <summary>Llamar dentro de OnGUI: Unity solo permite crear GUIStyle ahí.</summary>
        public static void Apply()
        {
            GUI.skin = HighLogic.Skin;
            if (Label != null && ReferenceEquals(_skin, HighLogic.Skin))
                return;

            _skin = HighLogic.Skin;
            Label = new GUIStyle(_skin.label) { richText = false, wordWrap = true };
            Bold = new GUIStyle(Label) { fontStyle = FontStyle.Bold };
            Muted = new GUIStyle(Label);
            Muted.normal.textColor = new Color(0.72f, 0.72f, 0.72f);

            Font font = _skin.font ?? GUI.skin.font;
            bool symbols = font != null && font.HasCharacter('✓') && font.HasCharacter('✗');
            Tick = symbols ? "✓" : "OK";
            Cross = symbols ? "✗" : "X";
            bool arrows = font != null && font.HasCharacter('▼') && font.HasCharacter('►');
            Open = arrows ? "▼" : "-";
            Closed = arrows ? "►" : "+";
        }

        /// <summary>Etiqueta coloreada reutilizando el mismo estilo (Unity pinta dentro de la llamada).</summary>
        public static void ColoredLabel(string text, Color color, params GUILayoutOption[] options)
        {
            Color previous = Label.normal.textColor;
            Label.normal.textColor = color;
            GUILayout.Label(text, Label, options);
            Label.normal.textColor = previous;
        }
    }
}
