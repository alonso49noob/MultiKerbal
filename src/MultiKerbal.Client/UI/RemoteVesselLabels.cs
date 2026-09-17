using MultiKerbal.Client.Systems;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Vessels;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// Nombre de la nave y de su dueño sobre las naves ajenas, en vuelo y en el mapa.
    /// (KSP ya tiene una clase llamada VesselLabels, de ahí el nombre.)
    /// </summary>
    internal static class RemoteVesselLabels
    {
        public const double MaxFlightDistance = 100000.0;
        private const float LabelWidth = 360f;
        private const float LabelHeight = 22f;

        private static GUIStyle _style;

        public static void Draw(ClientCore core)
        {
            LabelFilter filter = core.Settings.Labels;
            if (!filter.Enabled || !HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
                return;

            bool map = MapView.MapIsEnabled;
            Camera camera = map
                ? PlanetariumCamera.Camera
                : FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (camera == null)
                return;

            UiStyles.Apply();
            if (_style == null)
                _style = new GUIStyle(UiStyles.Label) { alignment = TextAnchor.MiddleCenter, wordWrap = false };

            Vessel active = FlightGlobals.ActiveVessel;
            foreach (Vessels.TrackedVessel tracked in core.Vessels.Tracked)
            {
                // Solo las ajenas: las nuestras, salvo que ahora las pilote otro.
                if (core.Vessels.IsPilotedByMe(tracked) || (core.Vessels.IsOwnedByMe(tracked) && !core.Vessels.IsPilotedByOther(tracked)))
                    continue;

                Vessel vessel = core.Vessels.VesselOf(tracked);
                if (vessel == null || vessel == active || !filter.Shows(vessel.vesselType))
                    continue;

                Vector3d position = vessel.transform.position;
                if (!map && active != null && (position - (Vector3d)active.transform.position).magnitude > MaxFlightDistance)
                    continue;

                Vector3 point = map ? (Vector3)ScaledSpace.LocalToScaledSpace(position) : (Vector3)position;
                Vector3 screen = camera.WorldToScreenPoint(point);
                if (screen.z <= 0f)
                    continue;

                _style.normal.textColor = core.Players.ColorOfOwner(tracked.OwnerName);
                string text = VesselPermissions.HasOwner(tracked.OwnerName)
                    ? $"{tracked.Name} · {tracked.OwnerName}"
                    : $"{tracked.Name} (sin dueño)";

                // Si la pilota alguien que no es su dueño, también se indica.
                PlayerInfo pilot = core.Players.Get(tracked.ControllerId);
                if (pilot != null && !VesselPermissions.IsOwner(tracked.OwnerName, pilot.Name))
                    text += $" — la pilota {pilot.Name}";

                var rect = new Rect(screen.x - (LabelWidth / 2f), Screen.height - screen.y - LabelHeight - 6f, LabelWidth, LabelHeight);
                GUI.Label(rect, text, _style);
            }
        }
    }
}
