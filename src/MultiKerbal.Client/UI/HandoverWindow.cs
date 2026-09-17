using System;
using System.Collections.Generic;
using MultiKerbal.Client.Vessels;
using MultiKerbal.Common.Messages;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Otro jugador pide el control de una nave que pilotas: aceptar se lo cede al momento.</summary>
    internal sealed class HandoverWindow
    {
        private const int WindowId = 0x4D4B0007;
        private const float Width = 420f;
        private const double ExpirySeconds = 60.0;

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private readonly List<Request> _requests = new List<Request>();
        private Rect _rect = new Rect(-1f, 120f, Width, 0f);

        public HandoverWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
        }

        public void Ask(VesselHandoverAskMessage message)
        {
            TrackedVessel tracked = _core.Vessels.Find(message.VesselId);
            string vesselName = tracked?.Name ?? "una nave";
            string playerName = _core.Players.Get(message.FromPlayerId)?.Name ?? "otro jugador";

            _requests.RemoveAll(r => r.VesselId == message.VesselId && r.PlayerId == message.FromPlayerId);
            _requests.Add(new Request
            {
                VesselId = message.VesselId,
                PlayerId = message.FromPlayerId,
                VesselName = vesselName,
                PlayerName = playerName,
                Expires = LocalClock.Now + ExpirySeconds,
            });

            ScreenMessages.PostScreenMessage(
                $"{playerName} pide el control de \"{vesselName}\"".Replace("<", "‹"),
                5f,
                ScreenMessageStyle.UPPER_CENTER);
        }

        public void Clear() => _requests.Clear();

        public void Draw()
        {
            _requests.RemoveAll(r => LocalClock.Now > r.Expires);
            if (_requests.Count == 0)
                return;

            UiStyles.Apply();
            if (_rect.x < 0f)
                _rect.x = (Screen.width - Width) / 2f;
            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "Petición de control", GUILayout.Width(Width));
        }

        private void DrawContents(int id)
        {
            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                Request request = _requests[i];
                GUILayout.Label($"{request.PlayerName} quiere pilotar \"{request.VesselName}\".", UiStyles.Label);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Dársela"))
                {
                    _core.Vessels.GrantControl(request.VesselId, request.PlayerId);
                    _requests.RemoveAt(i);
                }

                if (GUILayout.Button("Hacerlo copiloto"))
                {
                    _core.SharedControl.SetCopilot(request.VesselId, request.PlayerId, true);
                    _requests.RemoveAt(i);
                }

                if (GUILayout.Button("No", GUILayout.Width(70f)))
                    _requests.RemoveAt(i);
                GUILayout.EndHorizontal();
            }

            GUILayout.Label("Como copiloto sus mandos se suman a los tuyos; tú sigues llevando la nave.", UiStyles.Muted);
            GUI.DragWindow();
        }

        private sealed class Request
        {
            public Guid VesselId;
            public int PlayerId;
            public string VesselName;
            public string PlayerName;
            public double Expires;
        }
    }
}
