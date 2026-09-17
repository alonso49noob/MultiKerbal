using System;
using MultiKerbal.Client.Systems;
using MultiKerbal.Client.Vessels;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Vessels;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// Una nave: de quién es, quién puede usarla y quién la pilota ahora. Los cambios se piden al servidor,
    /// que es quien decide; la ventana muestra lo que este confirma.
    /// </summary>
    internal sealed class VesselOwnershipWindow
    {
        private const int WindowId = 0x4D4B0005;
        private const float Width = 480f;
        private const float PlayerColumn = 130f;

        private static readonly VesselAccess[] Accesses = { VesselAccess.Private, VesselAccess.Shared, VesselAccess.Public };

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(560f, 180f, Width, 0f);
        private Guid _vesselId;

        /// <summary>Acción que espera un segundo clic de confirmación (regalar, ceder, dejar sin dueño).</summary>
        private string _confirm;

        private bool _copilotActions = true;

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
            _rect = GUILayout.Window(WindowId, _rect, _drawContents, Loc.T("Nave: dueño y control", "Vessel: owner and control"), GUILayout.Width(Width));
        }

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
                !hasOwner
                    ? Loc.T("Sin dueño: cualquiera puede usarla, borrarla o reclamarla",
                            "No owner: anyone can use it, delete it or claim it")
                    : mine
                        ? Loc.T("Dueño: tú", "Owner: you")
                        : Loc.T($"Dueño: {tracked.OwnerName}", $"Owner: {tracked.OwnerName}"),
                UiStyles.Label);
            GUILayout.Space(6f);

            if (!hasOwner)
            {
                if (GUILayout.Button(Loc.T("Reclamarla (pasa a ser tuya)", "Claim it (it becomes yours)")))
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
                GUILayout.Label($"{UiStrings.Capitalize(UiStrings.Access(tracked.Access))}: {UiStrings.ExplainAccess(tracked.Access)}.", UiStyles.Label);
                GUILayout.Label(
                    Loc.T($"Solo {tracked.OwnerName} puede cambiarlo.", $"Only {tracked.OwnerName} can change this."),
                    UiStyles.Muted);
            }

            DrawControl(tracked);

            GUILayout.Space(8f);
            if (GUILayout.Button(Loc.T("Cerrar", "Close")))
                Visible = false;

            GUI.DragWindow();
        }

        private void DrawAccessChoice(TrackedVessel tracked)
        {
            GUILayout.Label(Loc.T("Quién puede usarla:", "Who can use it:"), UiStyles.Bold);
            foreach (VesselAccess access in Accesses)
            {
                bool selected = tracked.Access == access;
                string text = $"{UiStrings.Capitalize(UiStrings.Access(access))}: {UiStrings.ExplainAccess(access)}";
                if (GUILayout.Toggle(selected, text) && !selected)
                    _core.Vessels.RequestOwnerChange(tracked, tracked.OwnerName, access);
            }

            if (_core.Vessels.IsPilotedByOther(tracked) && tracked.Access != VesselAccess.Private)
            {
                GUILayout.Label(
                    Loc.T("Si la haces privada, quien la está pilotando saldrá de ella.",
                          "If you make it private, whoever is flying it will be dropped out."),
                    UiStyles.Muted);
            }
        }

        private void DrawGiveAway(TrackedVessel tracked)
        {
            GUILayout.Label(Loc.T("Regalarla a:", "Give it to:"), UiStyles.Bold);
            bool anyone = false;
            foreach (PlayerInfo player in _core.Players.All)
            {
                if (player.Id == _core.Players.LocalPlayerId)
                    continue;

                anyone = true;
                if (ConfirmButton(
                        "regalar:" + player.Name,
                        player.Name,
                        Loc.T($"¿Regalársela a {player.Name}? Pulsa otra vez", $"Give it to {player.Name}? Click again")))
                {
                    _core.Vessels.RequestOwnerChange(tracked, player.Name, tracked.Access);
                }
            }

            if (!anyone)
                GUILayout.Label(Loc.T("No hay otros jugadores conectados", "No other players connected"), UiStyles.Muted);

            GUILayout.Space(6f);
            if (ConfirmButton(
                    "abandonar",
                    Loc.T("Dejarla sin dueño", "Leave it without an owner"),
                    Loc.T("¿Seguro? Cualquiera podrá usarla, borrarla o reclamarla. Pulsa otra vez",
                          "Are you sure? Anyone will be able to use, delete or claim it. Click again")))
            {
                _core.Vessels.RequestOwnerChange(tracked, string.Empty, tracked.Access);
            }
        }

        /// <summary>Quién la pilota ahora: ceder el control, pedirlo, mirar o repartir los mandos con un copiloto.</summary>
        private void DrawControl(TrackedVessel tracked)
        {
            GUILayout.Space(10f);
            GUILayout.Label(Loc.T("Control", "Control"), UiStyles.Bold);

            if (_core.Vessels.IsPilotedByMe(tracked))
            {
                DrawPilotControls(tracked);
                return;
            }

            if (!_core.Vessels.IsPilotedByOther(tracked))
            {
                GUILayout.Label(
                    Loc.T("No la pilota nadie: acércate a ella y vuela con ella.",
                          "Nobody is flying it: get close and fly it."),
                    UiStyles.Muted);
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("Pedirle el control", "Ask to fly it")))
                _core.Vessels.RequestHandover(tracked);
            if (GUILayout.Button(Loc.T("Mirarla", "Watch it")))
            {
                if (!_core.Vessels.BeginSpectate(tracked, out string error))
                    ScreenMessages.PostScreenMessage(error, 5f, ScreenMessageStyle.UPPER_CENTER);
            }

            GUILayout.EndHorizontal();
            GUILayout.Label(
                Loc.T("Para acoplarte necesitas que quien la pilota te la ceda o que la suelte.",
                      "To dock you need whoever flies it to hand it over or let it go."),
                UiStyles.Muted);
        }

        private void DrawPilotControls(TrackedVessel tracked)
        {
            int copilotId = _core.SharedControl.CopilotOf(tracked.Id);
            PlayerInfo copilot = _core.Players.Get(copilotId);
            GUILayout.Label(
                copilot != null
                    ? Loc.T($"La pilotas tú, con {copilot.Name} de copiloto.", $"You are flying it, with {copilot.Name} as co-pilot.")
                    : Loc.T("La pilotas tú. Puedes cederla o repartir los mandos con un copiloto.",
                            "You are flying it. You can hand it over or share the controls with a co-pilot."),
                UiStyles.Label);

            bool anyone = false;
            foreach (PlayerInfo player in _core.Players.All)
            {
                if (player.Id == _core.Players.LocalPlayerId)
                    continue;

                anyone = true;
                GUILayout.BeginHorizontal();
                UiStyles.ColoredLabel(player.Name, PlayerRegistry.ColorOf(player), GUILayout.Width(PlayerColumn));
                if (ConfirmButton(
                        "ceder:" + player.Name,
                        Loc.T("Cederle la nave", "Hand it over"),
                        Loc.T($"¿Seguro? {player.Name} la pilotará. Pulsa otra vez", $"Are you sure? {player.Name} will fly it. Click again")))
                {
                    _core.Vessels.GrantControl(tracked.Id, player.Id);
                }

                if (copilotId == player.Id)
                {
                    if (GUILayout.Button(Loc.T("Quitar copiloto", "Remove co-pilot")))
                        _core.SharedControl.SetCopilot(tracked.Id, 0, false);
                }
                else if (GUILayout.Button(Loc.T("Copiloto", "Co-pilot")))
                {
                    _core.SharedControl.SetCopilot(tracked.Id, player.Id, _copilotActions);
                }

                GUILayout.EndHorizontal();
            }

            if (!anyone)
            {
                GUILayout.Label(Loc.T("No hay otros jugadores conectados", "No other players connected"), UiStyles.Muted);
                return;
            }

            bool actions = GUILayout.Toggle(
                _copilotActions,
                Loc.T("El copiloto también puede accionar etapas y grupos de acción",
                      "The co-pilot can also stage and use action groups"));
            if (actions != _copilotActions)
            {
                _copilotActions = actions;
                if (copilotId != 0)
                    _core.SharedControl.SetCopilot(tracked.Id, copilotId, actions);
            }

            GUILayout.Label(
                Loc.T("Los mandos del copiloto se suman a los tuyos; la nave la sigue simulando tu partida.",
                      "The co-pilot's controls add to yours; your game still simulates the vessel."),
                UiStyles.Muted);
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
