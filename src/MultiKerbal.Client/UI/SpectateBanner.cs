using MultiKerbal.Client.Vessels;
using MultiKerbal.Common.Messages;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Aviso fijo mientras miras una nave ajena o eres su copiloto: recuerda en qué modo estás.</summary>
    internal static class SpectateBanner
    {
        private const float Width = 470f;
        private const float Height = 62f;

        /// <summary>Una línea más cuando se mira y se copilota a la vez (el copiloto lejano mira la nave que copilota).</summary>
        private const float ExtraLineHeight = 24f;

        public static void Draw(ClientCore core)
        {
            TrackedVessel spectated = core.Vessels.Find(core.Vessels.SpectatingId);
            TrackedVessel copiloted = core.Vessels.Find(core.SharedControl.CopilotingId);
            if (spectated == null && copiloted == null)
                return;

            UiStyles.Apply();
            float height = spectated != null && copiloted != null ? Height + ExtraLineHeight : Height;
            GUILayout.BeginArea(new Rect((Screen.width - Width) / 2f, 60f, Width, height), GUI.skin.box);

            if (spectated != null)
            {
                string pilot = core.Players.Get(spectated.ControllerId)?.Name ?? Loc.T("nadie", "nobody");
                UiStyles.ColoredLabel(
                    Loc.T($"Mirando \"{spectated.Name}\" — la pilota {pilot}", $"Watching \"{spectated.Name}\" — flown by {pilot}"),
                    UiStyles.Warning);
            }

            if (copiloted != null)
            {
                GUILayout.Label(
                    core.SharedControl.CanAct
                        ? Loc.T($"Copilotas \"{copiloted.Name}\": mandos, etapas y grupos de acción",
                                $"Co-piloting \"{copiloted.Name}\": controls, staging and action groups")
                        : Loc.T($"Copilotas \"{copiloted.Name}\": solo los mandos de vuelo",
                                $"Co-piloting \"{copiloted.Name}\": flight controls only"),
                    UiStyles.Label);
            }

            GUILayout.BeginHorizontal();
            if (spectated != null && GUILayout.Button(Loc.T("Dejar de mirar", "Stop watching"), GUILayout.Width(160f)))
                core.Vessels.StopSpectate();
            if (copiloted != null && GUILayout.Button(Loc.T("Dejar de copilotar", "Stop co-piloting"), GUILayout.Width(180f)))
                core.SharedControl.SetCopilot(copiloted.Id, 0, false);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
