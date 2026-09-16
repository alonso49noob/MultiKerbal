using MultiKerbal.Client.Systems;
using MultiKerbal.Common.Messages;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// Nombre de la nave y de su dueño sobre las naves ajenas, en vuelo y en el mapa.
    /// (KSP ya tiene una clase llamada VesselLabels, de ahí el nombre.)
    /// </summary>
    internal static class RemoteVesselLabels
    {
        private const double MaxFlightDistance = 100000.0;
        private const float LabelWidth = 260f;
        private const float LabelHeight = 22f;

        private static GUIStyle _style;

        public static void Draw(ClientCore core)
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
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
                if (core.Vessels.IsMine(tracked))
                    continue;

                Vessel vessel = core.Vessels.VesselOf(tracked);
                if (vessel == null || vessel == active)
                    continue;

                Vector3d position = vessel.transform.position;
                if (!map && active != null && (position - (Vector3d)active.transform.position).magnitude > MaxFlightDistance)
                    continue;

                Vector3 point = map ? (Vector3)ScaledSpace.LocalToScaledSpace(position) : (Vector3)position;
                Vector3 screen = camera.WorldToScreenPoint(point);
                if (screen.z <= 0f)
                    continue;

                PlayerInfo owner = core.Players.Get(tracked.OwnerId);
                _style.normal.textColor = tracked.OwnerId == 0 ? Color.gray : PlayerRegistry.ColorOf(owner);
                string text = tracked.OwnerId == 0
                    ? $"{tracked.Name} (sin dueño)"
                    : $"{tracked.Name} · {owner?.Name ?? "otro jugador"}";

                var rect = new Rect(screen.x - (LabelWidth / 2f), Screen.height - screen.y - LabelHeight - 6f, LabelWidth, LabelHeight);
                GUI.Label(rect, text, _style);
            }
        }
    }
}
