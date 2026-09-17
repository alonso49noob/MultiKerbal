using MultiKerbal.Client.Vessels;
using MultiKerbal.Common.Messages;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Aviso fijo mientras miras una nave ajena o eres su copiloto: recuerda en qué modo estás.</summary>
    internal static class SpectateBanner
    {
        private const float Width = 460f;
        private const float Height = 58f;

        public static void Draw(ClientCore core)
        {
            TrackedVessel spectated = core.Vessels.Find(core.Vessels.SpectatingId);
            TrackedVessel copiloted = core.Vessels.Find(core.SharedControl.CopilotingId);
            if (spectated == null && copiloted == null)
                return;

            UiStyles.Apply();
            var area = new Rect((Screen.width - Width) / 2f, 60f, Width, Height);
            GUILayout.BeginArea(area, GUI.skin.box);

            if (spectated != null)
            {
                string pilot = core.Players.Get(spectated.ControllerId)?.Name ?? "nadie";
                GUILayout.Label($"Mirando \"{spectated.Name}\" — la pilota {pilot}", UiStyles.Bold);
            }

            if (copiloted != null)
            {
                bool actions = core.SharedControl.CanAct;
                GUILayout.Label(
                    $"Copilotas \"{copiloted.Name}\": tus mandos se suman a los del piloto"
                    + (actions ? " (también etapas y grupos de acción)" : " (sin etapas ni grupos de acción)"),
                    UiStyles.Label);
            }

            GUILayout.BeginHorizontal();
            if (spectated != null && GUILayout.Button("Dejar de mirar", GUILayout.Width(150f)))
                core.Vessels.StopSpectate();
            if (copiloted != null && GUILayout.Button("Dejar de copilotar", GUILayout.Width(170f)))
                core.SharedControl.SetCopilot(copiloted.Id, 0, false);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
