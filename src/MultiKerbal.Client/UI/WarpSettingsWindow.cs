using MultiKerbal.Common.Time;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Qué hacer cuando otro jugador pide acelerar el tiempo. Los cambios se guardan al momento.</summary>
    internal sealed class WarpSettingsWindow
    {
        private const int WindowId = 0x4D4B0003;
        private const float Width = 460f;
        private const float Indent = 22f;

        private static readonly double[] RateChoices = { 5, 10, 50, 100, 1000, 10000, 100000 };
        private static readonly double[] IdleChoices = { 30, 60, 120, 300, 600 };

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(520f, 140f, Width, 0f);

        public WarpSettingsWindow(ClientCore core)
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
            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "Ajustes de warp", GUILayout.Width(Width));
        }

        private static bool Toggle(ref bool value, string label, bool indented = false)
        {
            GUILayout.BeginHorizontal();
            if (indented)
                GUILayout.Space(Indent);
            bool result = GUILayout.Toggle(value, label);
            GUILayout.EndHorizontal();

            if (result == value)
                return false;

            value = result;
            return true;
        }

        private static void Note(string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(Indent);
            GUILayout.Label(text, UiStyles.Muted);
            GUILayout.EndHorizontal();
        }

        /// <summary>Selector con flechas para un valor de una lista.</summary>
        private static bool Stepper(ref double value, double[] choices, string label, string text)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Space(Indent);
            GUILayout.Label(label, UiStyles.Label, GUILayout.Width(52f));
            if (GUILayout.Button("<", GUILayout.Width(28f)))
            {
                value = Step(value, choices, -1);
                changed = true;
            }

            GUILayout.Label(text, UiStyles.Label, GUILayout.Width(80f));
            if (GUILayout.Button(">", GUILayout.Width(28f)))
            {
                value = Step(value, choices, +1);
                changed = true;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return changed;
        }

        private static double Step(double current, double[] choices, int direction)
        {
            int index = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                if (choices[i] <= current)
                    index = i;
            }

            return choices[Mathf.Clamp(index + direction, 0, choices.Length - 1)];
        }

        private void DrawContents(int id)
        {
            WarpPolicy policy = _core.Settings.Warp;
            bool changed = false;

            GUILayout.Label("Cuando otro jugador quiere acelerar el tiempo:", UiStyles.Bold);

            changed |= Toggle(ref policy.AutoAccept, "Aceptar automáticamente");
            if (policy.AutoDeny)
                Note("Desactivado mientras el rechazo automático esté activado.");
            GUI.enabled = policy.AutoAccept && !policy.AutoDeny;
            changed |= Stepper(ref policy.AcceptMaxRate, RateChoices, "Hasta", $"x{policy.AcceptMaxRate:0}");
            Note("Solo si se cumple todo lo marcado:");
            changed |= Toggle(ref policy.AcceptOnlyOutsideFlight, "No estoy pilotando (Centro Espacial o estación de seguimiento)", true);
            changed |= Toggle(ref policy.AcceptOnlyStable, "Mi nave está en órbita, posada o amerizada", true);
            changed |= Toggle(ref policy.AcceptOnlyEnginesOff, "Los motores están apagados", true);
            changed |= Toggle(ref policy.AcceptOnlyAlone, "No hay otras naves a menos de 2,5 km", true);
            GUI.enabled = true;

            GUILayout.Space(10f);
            GUI.enabled = !policy.AutoDeny;
            changed |= Toggle(ref policy.AcceptWhenIdle, "Aceptar si estoy ausente (sin tocar teclado ni ratón)");
            GUI.enabled = policy.AcceptWhenIdle && !policy.AutoDeny;
            changed |= Stepper(ref policy.IdleSeconds, IdleChoices, "Tras", WarpPolicyEvaluator.FormatSeconds(policy.IdleSeconds));
            Note("Sin mirar las condiciones de arriba, hasta el mismo máximo.");
            GUI.enabled = true;

            GUILayout.Space(10f);
            changed |= Toggle(ref policy.AutoDeny, "Rechazar automáticamente (mientras esté activo no se acepta nada solo)");
            GUI.enabled = policy.AutoDeny;
            Note("Rechaza si se cumple cualquiera de lo marcado; sin nada marcado, siempre:");
            changed |= Toggle(ref policy.DenyWhileFlying, "Estoy pilotando", true);
            changed |= Toggle(ref policy.DenyAtSpaceCenter, "Estoy en el Centro Espacial", true);
            changed |= Toggle(ref policy.DenyInAtmosphere, "Mi nave está en la atmósfera", true);
            changed |= Toggle(ref policy.DenyNearVessels, "Hay otras naves a menos de 2,5 km", true);
            GUI.enabled = true;

            GUILayout.Space(10f);
            GUILayout.Label(_core.Warp.PolicyStatus, UiStyles.Label);
            GUILayout.Label("El warp físico (x2 a x4) nunca se acepta automáticamente.", UiStyles.Muted);

            if (GUILayout.Button("Cerrar"))
                Visible = false;

            if (changed)
                _core.Settings.Save();

            GUI.DragWindow();
        }
    }
}
