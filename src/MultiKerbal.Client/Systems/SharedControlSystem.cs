using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using UnityEngine;

namespace MultiKerbal.Client.Systems
{
    /// <summary>Lo que se sabe del copiloto de una nave y sus últimos mandos.</summary>
    internal sealed class CopilotSeat
    {
        public int CopilotId;
        public bool AllowActions;
        public VesselInputMessage Input;
        public double InputTime;
    }

    /// <summary>
    /// Control compartido. Quien pilota una nave puede nombrar copiloto a otro jugador: los mandos del copiloto
    /// viajan por la red y los aplica el cliente de quien pilota, que sigue siendo el único que simula la nave.
    /// Los ejes se suman a los del piloto (los dos pueden corregir a la vez) y el acelerador sube o baja.
    /// </summary>
    internal sealed class SharedControlSystem
    {
        private const double SendIntervalSeconds = 0.05;
        private const double InputExpirySeconds = 1.0;
        private const float ThrottleRatePerSecond = 0.6f;

        private static readonly KeyValuePair<VesselAction, KSPActionGroup>[] ActionGroups =
        {
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Gear, KSPActionGroup.Gear),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Brakes, KSPActionGroup.Brakes),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Lights, KSPActionGroup.Light),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Rcs, KSPActionGroup.RCS),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Sas, KSPActionGroup.SAS),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Abort, KSPActionGroup.Abort),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom01, KSPActionGroup.Custom01),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom02, KSPActionGroup.Custom02),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom03, KSPActionGroup.Custom03),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom04, KSPActionGroup.Custom04),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom05, KSPActionGroup.Custom05),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom06, KSPActionGroup.Custom06),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom07, KSPActionGroup.Custom07),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom08, KSPActionGroup.Custom08),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom09, KSPActionGroup.Custom09),
            new KeyValuePair<VesselAction, KSPActionGroup>(VesselAction.Custom10, KSPActionGroup.Custom10),
        };

        private readonly Func<int> _localPlayerId;
        private readonly Func<Guid, bool> _pilotedByMe;
        private readonly Func<bool> _typing;
        private readonly Action<IMessage, Delivery> _send;
        private readonly Dictionary<Guid, CopilotSeat> _seats = new Dictionary<Guid, CopilotSeat>();
        private readonly FlightInputCallback _applyCopilot;
        private Vessel _hooked;
        private Guid _hookedId;
        private double _nextSend;
        private bool _sentZero = true;

        public SharedControlSystem(
            Func<int> localPlayerId,
            Func<Guid, bool> pilotedByMe,
            Func<bool> typing,
            Action<IMessage, Delivery> send)
        {
            _localPlayerId = localPlayerId;
            _pilotedByMe = pilotedByMe;
            _typing = typing;
            _send = send;
            _applyCopilot = ApplyCopilot;
        }

        /// <summary>Nave de la que soy copiloto ahora mismo (vacío si ninguna).</summary>
        public Guid CopilotingId { get; private set; }

        public CopilotSeat SeatOf(Guid vesselId) => _seats.TryGetValue(vesselId, out CopilotSeat seat) ? seat : null;

        public int CopilotOf(Guid vesselId) => SeatOf(vesselId)?.CopilotId ?? 0;

        public bool CanAct => CopilotingId != Guid.Empty && (SeatOf(CopilotingId)?.AllowActions ?? false);

        /// <summary>Quien pilota nombra copiloto (playerId 0 para quitarlo). El copiloto puede usarlo para renunciar.</summary>
        public void SetCopilot(Guid vesselId, int playerId, bool allowActions)
        {
            _send(
                new VesselCopilotMessage { VesselId = vesselId, CopilotId = playerId, AllowActions = allowActions },
                Delivery.Reliable);
        }

        public void Handle(IMessage message)
        {
            switch (message)
            {
                case VesselCopilotMessage copilot:
                    OnCopilot(copilot);
                    break;
                case VesselInputMessage input:
                    OnInput(input);
                    break;
                case VesselActionMessage action:
                    OnAction(action);
                    break;
            }
        }

        public void Reset()
        {
            Unhook();
            _seats.Clear();
            CopilotingId = Guid.Empty;
        }

        public void Update()
        {
            UpdatePilotHook();
            SendMyInput();
        }

        private static float Axis(KeyBinding positive, KeyBinding negative) =>
            (positive.GetKey(true) ? 1f : 0f) - (negative.GetKey(true) ? 1f : 0f);

        private static bool IsZero(VesselInputMessage input) =>
            Mathf.Approximately(input.Pitch, 0f)
            && Mathf.Approximately(input.Yaw, 0f)
            && Mathf.Approximately(input.Roll, 0f)
            && Mathf.Approximately(input.Throttle, 0f)
            && Mathf.Approximately(input.WheelSteer, 0f)
            && Mathf.Approximately(input.WheelThrottle, 0f);

        private void OnCopilot(VesselCopilotMessage message)
        {
            CopilotSeat seat = SeatOf(message.VesselId);
            if (seat == null)
            {
                seat = new CopilotSeat();
                _seats[message.VesselId] = seat;
            }

            seat.CopilotId = message.CopilotId;
            seat.AllowActions = message.AllowActions;
            seat.Input = null;

            bool mine = message.CopilotId != 0 && message.CopilotId == _localPlayerId();
            if (mine)
                CopilotingId = message.VesselId;
            else if (CopilotingId == message.VesselId)
                CopilotingId = Guid.Empty;
        }

        private void OnInput(VesselInputMessage message)
        {
            CopilotSeat seat = SeatOf(message.VesselId);
            if (seat == null || !_pilotedByMe(message.VesselId))
                return;

            seat.Input = message;
            seat.InputTime = LocalClock.Now;
        }

        private void OnAction(VesselActionMessage message)
        {
            if (!_pilotedByMe(message.VesselId))
                return;

            Vessel vessel = FlightGlobals.FindVessel(message.VesselId);
            if (vessel == null)
                return;

            if (message.Action == VesselAction.Stage)
            {
                // Las etapas las lanza KSP sobre la nave activa; en otra nave se ignora.
                if (vessel == FlightGlobals.ActiveVessel)
                    StageManager.ActivateNextStage();
                return;
            }

            foreach (KeyValuePair<VesselAction, KSPActionGroup> pair in ActionGroups)
            {
                if (pair.Key == message.Action)
                {
                    vessel.ActionGroups.ToggleGroup(pair.Value);
                    return;
                }
            }
        }

        /// <summary>Engancha los mandos del copiloto a la nave que pilotamos (KSP llama a esto en cada paso de física).</summary>
        private void UpdatePilotHook()
        {
            Guid target = Guid.Empty;
            foreach (KeyValuePair<Guid, CopilotSeat> pair in _seats)
            {
                if (pair.Value.CopilotId != 0 && _pilotedByMe(pair.Key))
                {
                    target = pair.Key;
                    break;
                }
            }

            Vessel vessel = target == Guid.Empty ? null : FlightGlobals.FindVessel(target);
            if (vessel == _hooked)
                return;

            Unhook();
            if (vessel == null)
                return;

            _hooked = vessel;
            _hookedId = target;
            vessel.OnFlyByWire += _applyCopilot;
        }

        private void Unhook()
        {
            if (_hooked != null)
                _hooked.OnFlyByWire -= _applyCopilot;
            _hooked = null;
            _hookedId = Guid.Empty;
        }

        private void ApplyCopilot(FlightCtrlState state)
        {
            CopilotSeat seat = SeatOf(_hookedId);
            if (seat?.Input == null || LocalClock.Now - seat.InputTime > InputExpirySeconds)
                return;

            VesselInputMessage input = seat.Input;
            state.pitch = Mathf.Clamp(state.pitch + input.Pitch, -1f, 1f);
            state.yaw = Mathf.Clamp(state.yaw + input.Yaw, -1f, 1f);
            state.roll = Mathf.Clamp(state.roll + input.Roll, -1f, 1f);
            state.wheelSteer = Mathf.Clamp(state.wheelSteer + input.WheelSteer, -1f, 1f);
            state.wheelThrottle = Mathf.Clamp(state.wheelThrottle + input.WheelThrottle, -1f, 1f);
            if (!Mathf.Approximately(input.Throttle, 0f))
                state.mainThrottle = Mathf.Clamp01(state.mainThrottle + (input.Throttle * ThrottleRatePerSecond * Time.fixedDeltaTime));
        }

        /// <summary>Si soy copiloto, mando mis teclas a quien pilota.</summary>
        private void SendMyInput()
        {
            if (CopilotingId == Guid.Empty || !HighLogic.LoadedSceneIsFlight || _typing())
                return;

            double now = LocalClock.Now;
            if (now < _nextSend)
                return;

            _nextSend = now + SendIntervalSeconds;
            var input = new VesselInputMessage
            {
                VesselId = CopilotingId,
                Pitch = Axis(GameSettings.PITCH_UP, GameSettings.PITCH_DOWN),
                Yaw = Axis(GameSettings.YAW_RIGHT, GameSettings.YAW_LEFT),
                Roll = Axis(GameSettings.ROLL_RIGHT, GameSettings.ROLL_LEFT),
                Throttle = Axis(GameSettings.THROTTLE_UP, GameSettings.THROTTLE_DOWN),
                WheelSteer = Axis(GameSettings.WHEEL_STEER_RIGHT, GameSettings.WHEEL_STEER_LEFT),
                WheelThrottle = Axis(GameSettings.WHEEL_THROTTLE_UP, GameSettings.WHEEL_THROTTLE_DOWN),
            };

            bool zero = IsZero(input);
            if (!zero || !_sentZero)
            {
                // Soltar los mandos tiene que llegar sí o sí: el último envío va por TCP.
                _send(input, zero ? Delivery.Reliable : Delivery.Unreliable);
                _sentZero = zero;
            }

            SendMyActions();
        }

        private void SendMyActions()
        {
            if (!CanAct)
                return;

            if (GameSettings.LAUNCH_STAGES.GetKeyDown(true))
                SendAction(VesselAction.Stage);
            if (GameSettings.LANDING_GEAR.GetKeyDown(true))
                SendAction(VesselAction.Gear);
            if (GameSettings.BRAKES.GetKeyDown(true))
                SendAction(VesselAction.Brakes);
            if (GameSettings.HEADLIGHT_TOGGLE.GetKeyDown(true))
                SendAction(VesselAction.Lights);
            if (GameSettings.RCS_TOGGLE.GetKeyDown(true))
                SendAction(VesselAction.Rcs);
            if (GameSettings.SAS_TOGGLE.GetKeyDown(true))
                SendAction(VesselAction.Sas);
            if (GameSettings.AbortActionGroup.GetKeyDown(true))
                SendAction(VesselAction.Abort);

            CheckCustomGroup(GameSettings.CustomActionGroup1, VesselAction.Custom01);
            CheckCustomGroup(GameSettings.CustomActionGroup2, VesselAction.Custom02);
            CheckCustomGroup(GameSettings.CustomActionGroup3, VesselAction.Custom03);
            CheckCustomGroup(GameSettings.CustomActionGroup4, VesselAction.Custom04);
            CheckCustomGroup(GameSettings.CustomActionGroup5, VesselAction.Custom05);
            CheckCustomGroup(GameSettings.CustomActionGroup6, VesselAction.Custom06);
            CheckCustomGroup(GameSettings.CustomActionGroup7, VesselAction.Custom07);
            CheckCustomGroup(GameSettings.CustomActionGroup8, VesselAction.Custom08);
            CheckCustomGroup(GameSettings.CustomActionGroup9, VesselAction.Custom09);
            CheckCustomGroup(GameSettings.CustomActionGroup10, VesselAction.Custom10);
        }

        private void CheckCustomGroup(KeyBinding binding, VesselAction action)
        {
            if (binding.GetKeyDown(true))
                SendAction(action);
        }

        private void SendAction(VesselAction action) =>
            _send(new VesselActionMessage { VesselId = CopilotingId, Action = action }, Delivery.Reliable);
    }
}
