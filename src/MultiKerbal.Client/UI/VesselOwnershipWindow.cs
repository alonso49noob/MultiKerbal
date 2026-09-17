using System;
using MultiKerbal.Client.Vessels;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Vessels;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// Dueño y acceso de una nave: quién puede pilotarla, recuperarla o borrarla. Los cambios se piden al servidor,
    /// que es quien decide; la ventana muestra lo que este confirma.
    /// </summary>
    internal sealed class VesselOwnershipWindow
    {
        private const int WindowId = 0x4D4B0005;
        private const float Width = 480f;

        private static readonly VesselAccess[] Accesses = { VesselAccess.Private, VesselAccess.Shared, VesselAccess.Public };

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(560f, 180f, Width, 0f);
        private Guid _vesselId;

        /// <summary>Acción que espera un segundo clic de confirmación (regalar, dejar sin dueño).</summary>
        private string _confirm;

        public VesselOwnershipWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
        }

        public bool Visible { get; set; }

        /// <summary>Abre la ventana para esa nave; si ya estaba abierta con ella, la cierra.</summary>
        public void Toggle(Guid vesselId)
        {
            bool sameVessel = Visible && vesselId == _vesselId;
            _vesselId = vesselId;
            _confirm = null;
            Visible = !sameVessel;
        }

        public void Draw()
        {
            if (!Visible)
                return;

            if (_core.Vessels.Find(_vesselId) == null)
            {
                Visible = false; // La nave ya no existe.
                return;
            }

            UiStyles.Apply();
            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "Propiedad de la nave", GUILayout.Width(Width));
        }

        public static string Explain(VesselAccess access)
        {
            switch (access)
            {
                case VesselAccess.Private:
                    return "solo el dueño puede pilotarla, recuperarla o borrarla";
                case VesselAccess.Public:
                    return "cualquiera puede pilotarla, recuperarla o borrarla";
                default:
                    return "cualquiera puede pilotarla; recuperarla o borrarla, solo el dueño";
            }
        }

        private static string Capitalize(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        private void DrawContents(int id)
        {
            TrackedVessel tracked = _core.Vessels.Find(_vesselId);
            if (tracked == null)
            {
                GUI.DragWindow();
                return;
            }

            bool hasOwner = VesselPermissions.HasOwner(tracked.OwnerName);
            bool mine = _core.Vessels.IsOwnedByMe(tracked);

            UiStyles.ColoredLabel(tracked.Name, _core.Players.ColorOfOwner(tracked.OwnerName));
            GUILayout.Label(
                !hasOwner ? "Sin dueño: cualquiera puede usarla, borrarla o reclamarla" : mine ? "Dueño: tú" : $"Dueño: {tracked.OwnerName}",
                UiStyles.Label);
            GUILayout.Label(DescribePilot(tracked), UiStyles.Muted);
            GUILayout.Space(6f);

            if (!hasOwner)
            {
                if (GUILayout.Button("Reclamarla (pasa a ser tuya)"))
                    _core.Vessels.RequestOwnerChange(tracked, _core.LocalPlayerName, _core.Settings.DefaultAccess);
            }
            else if (mine)
            {
                DrawAccessChoice(tracked);
                GUILayout.Space(8f);
                DrawGiveAway(tracked);
            }
            else
            {
                GUILayout.Label($"{Capitalize(VesselPermissions.Describe(tracked.Access))}: {Explain(tracked.Access)}.", UiStyles.Label);
                GUILayout.Label($"Solo {tracked.OwnerName} puede cambiarlo.", UiStyles.Muted);
            }

            GUILayout.Space(8f);
            if (GUILayout.Button("Cerrar"))
                Visible = false;

            GUI.DragWindow();
        }

        private string DescribePilot(TrackedVessel tracked)
        {
            if (_core.Vessels.IsPilotedByMe(tracked))
                return "La estás pilotando tú";
            PlayerInfo pilot = _core.Players.Get(tracked.ControllerId);
            return pilot != null ? $"Ahora la pilota {pilot.Name}" : "Nadie la está pilotando";
        }

        private void DrawAccessChoice(TrackedVessel tracked)
        {
            GUILayout.Label("Quién puede usarla:", UiStyles.Bold);
            foreach (VesselAccess access in Accesses)
            {
                bool selected = tracked.Access == access;
                string text = $"{Capitalize(VesselPermissions.Describe(access))}: {Explain(access)}";
                if (GUILayout.Toggle(selected, text) && !selected)
                    _core.Vessels.RequestOwnerChange(tracked, tracked.OwnerName, access);
            }

            if (_core.Vessels.IsPilotedByOther(tracked) && tracked.Access != VesselAccess.Private)
                GUILayout.Label("Si la haces privada, quien la está pilotando saldrá de ella.", UiStyles.Muted);
        }

        private void DrawGiveAway(TrackedVessel tracked)
        {
            GUILayout.Label("Regalarla a:", UiStyles.Bold);
            bool anyone = false;
            foreach (PlayerInfo player in _core.Players.All)
            {
                if (player.Id == _core.Players.LocalPlayerId)
                    continue;

                anyone = true;
                if (ConfirmButton("regalar:" + player.Name, player.Name, $"¿Regalársela a {player.Name}? Pulsa otra vez"))
                    _core.Vessels.RequestOwnerChange(tracked, player.Name, tracked.Access);
            }

            if (!anyone)
                GUILayout.Label("No hay otros jugadores conectados", UiStyles.Muted);

            GUILayout.Space(6f);
            if (ConfirmButton("abandonar", "Dejarla sin dueño", "¿Seguro? Cualquiera podrá usarla, borrarla o reclamarla. Pulsa otra vez"))
                _core.Vessels.RequestOwnerChange(tracked, string.Empty, tracked.Access);
        }

        /// <summary>Botón que pide un segundo clic: estas acciones no las puedes deshacer tú solo.</summary>
        private bool ConfirmButton(string key, string text, string confirmText)
        {
            bool confirming = _confirm == key;
            if (!GUILayout.Button(confirming ? confirmText : text))
                return false;

            _confirm = confirming ? null : key;
            return confirming;
        }
    }
}
