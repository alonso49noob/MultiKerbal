using System;
using System.Collections.Generic;
using System.Linq;
using KSP.Localization;
using KSP.UI.Screens;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Client.Vessels
{
    /// <summary>
    /// Mantiene las naves de la partida local alineadas con el servidor.
    /// <list type="bullet">
    /// <item>Naves que pilotamos: se publican al crearse o cambiar de estructura y se envía su estado periódicamente.</item>
    /// <item>Las demás: marionetas empaquetadas que siguen el último estado recibido.</item>
    /// </list>
    /// "Local" significa que la pilotamos ahora (el control), que es distinto de ser su dueño (ver <see cref="VesselPermissions"/>).
    /// Solo actúa en escenas con naves vivas (Centro Espacial, estación de seguimiento y vuelo).
    /// </summary>
    internal sealed class VesselSyncSystem
    {
        private const double SceneSettleSeconds = 1.0;
        private const double ScanIntervalSeconds = 1.0;
        private const double PhysicsUpdateIntervalSeconds = 0.1;
        private const double RailsUpdateIntervalSeconds = 5.0;
        private const double ProtoRefreshSeconds = 30.0;
        private const double RespawnRetrySeconds = 10.0;
        private const float NoticeSeconds = 5f;

        /// <summary>KSP carga las naves a 2,5 km: el control se pide algo antes para que llegue a tiempo.</summary>
        private const double TakeoverDistance = 2200.0;

        private const string SpectateLockId = "MultiKerbalSpectate";

        private const ControlTypes SpectateLocks =
            ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.STAGING | ControlTypes.CUSTOM_ACTION_GROUPS | ControlTypes.ACTIONS_SHIP;

        private readonly Func<int> _localPlayerId;
        private readonly Func<string> _localPlayerName;
        private readonly Func<int, string> _playerName;
        private readonly Func<VesselAccess> _defaultAccess;
        private readonly Action<IMessage, Delivery> _send;
        private readonly Dictionary<Guid, TrackedVessel> _tracked = new Dictionary<Guid, TrackedVessel>();
        private readonly HashSet<Guid> _removed = new HashSet<Guid>();
        private readonly List<TrackedVessel> _scratch = new List<TrackedVessel>();

        private GameScenes _scene = GameScenes.LOADING;
        private double _sceneEnteredAt;
        private bool _reconciled;
        private double _nextScan;
        private Guid _activeVesselId;
        private Guid _previousActiveVesselId;
        private int _suppressDestroyEvents;
        private bool _eventsRegistered;
        private Guid _guardedVessel;
        private bool _guardBlockedButtons;

        public VesselSyncSystem(
            Func<int> localPlayerId,
            Func<string> localPlayerName,
            Func<int, string> playerName,
            Func<VesselAccess> defaultAccess,
            Action<IMessage, Delivery> send)
        {
            _localPlayerId = localPlayerId;
            _localPlayerName = localPlayerName;
            _playerName = playerName;
            _defaultAccess = defaultAccess;
            _send = send;
        }

        public int TotalVessels => _tracked.Count;

        /// <summary>Naves conocidas del universo (para la interfaz).</summary>
        public IEnumerable<TrackedVessel> Tracked => _tracked.Values;

        public TrackedVessel Find(Guid id) => _tracked.TryGetValue(id, out TrackedVessel tracked) ? tracked : null;

        /// <summary>La pilotamos nosotros ahora.</summary>
        public bool IsPilotedByMe(TrackedVessel tracked) => IsLocal(tracked);

        public bool IsOwnedByMe(TrackedVessel tracked) => VesselPermissions.IsOwner(tracked.OwnerName, _localPlayerName());

        /// <summary>La pilota otro jugador ahora.</summary>
        public bool IsPilotedByOther(TrackedVessel tracked) => tracked.ControllerId != 0 && !IsLocal(tracked);

        public bool CanPilot(TrackedVessel tracked) =>
            !IsPilotedByOther(tracked) && VesselPermissions.CanPilot(tracked.OwnerName, tracked.Access, _localPlayerName());

        public bool CanRemove(TrackedVessel tracked) =>
            IsLocal(tracked)
            || (tracked.ControllerId == 0 && VesselPermissions.CanRemove(tracked.OwnerName, tracked.Access, _localPlayerName()));

        public bool CanChangeOwnership(TrackedVessel tracked) =>
            VesselPermissions.CanChangeOwnership(tracked.OwnerName, _localPlayerName());

        /// <summary>Nave que estamos mirando sin pilotarla (vacío si ninguna).</summary>
        public Guid SpectatingId { get; private set; }

        public bool IsSpectating(Vessel vessel) => vessel != null && SpectatingId != Guid.Empty && vessel.id == SpectatingId;

        /// <summary>
        /// Mirar una nave que pilota otro jugador, con los mandos bloqueados. Solo funciona si está cargada
        /// (a menos de 2,5 km): más lejos KSP tendría que recargar la escena alrededor de ella.
        /// </summary>
        public bool BeginSpectate(TrackedVessel tracked, out string error)
        {
            Vessel vessel = LiveVessel(tracked);
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || vessel == null || !vessel.loaded)
            {
                error = "Solo puedes mirarla si estás volando a menos de 2,5 km de ella";
                return false;
            }

            if (vessel == FlightGlobals.ActiveVessel || FlightGlobals.SetActiveVessel(vessel))
            {
                SpectatingId = tracked.Id;
                InputLockManager.SetControlLock(SpectateLocks, SpectateLockId);
                error = null;
                ClientLog.Info($"Mirando \"{tracked.Name}\"");
                return true;
            }

            error = "KSP no ha dejado cambiar la cámara a esa nave";
            return false;
        }

        /// <summary>Deja de mirar y vuelve a una nave propia (o al Centro Espacial si no hay ninguna).</summary>
        public void StopSpectate()
        {
            if (SpectatingId == Guid.Empty)
                return;

            Guid spectated = SpectatingId;
            SpectatingId = Guid.Empty;
            InputLockManager.RemoveControlLock(SpectateLockId);

            // Si mientras mirábamos nos dieron el control, nos quedamos en ella.
            Vessel active = FlightGlobals.ActiveVessel;
            bool nowMine = _tracked.TryGetValue(spectated, out TrackedVessel tracked) && IsLocal(tracked);
            if (active != null && active.id == spectated && !nowMine)
                ReturnToOwnVessel();
        }

        /// <summary>Pide a quien la pilota que nos ceda el control (por ejemplo, para acoplarse).</summary>
        public void RequestHandover(TrackedVessel tracked)
        {
            _send(new VesselHandoverRequestMessage { VesselId = tracked.Id }, Delivery.Reliable);
            ScreenMessages.PostScreenMessage(
                $"Pedido el control de \"{tracked.Name}\" a {_playerName(tracked.ControllerId)}".Replace("<", "‹"),
                NoticeSeconds,
                ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Le damos el control de una nave que pilotamos a otro jugador.</summary>
        public void GrantControl(Guid vesselId, int playerId) =>
            _send(new VesselHandoverGrantMessage { VesselId = vesselId, ToPlayerId = playerId }, Delivery.Reliable);

        /// <summary>Pide al servidor otro dueño (uno mismo, otro jugador o vacío) y acceso. El cambio llega de vuelta si se acepta.</summary>
        public void RequestOwnerChange(TrackedVessel tracked, string ownerName, VesselAccess access)
        {
            _send(new VesselOwnerRequestMessage { VesselId = tracked.Id, OwnerName = ownerName ?? string.Empty, Access = access }, Delivery.Reliable);
        }

        /// <summary>La nave en la escena actual, o null si no está cargada.</summary>
        public Vessel VesselOf(TrackedVessel tracked) => LiveVessel(tracked);

        public void RegisterEvents()
        {
            if (_eventsRegistered)
                return;

            _eventsRegistered = true;
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselRecovered.Add(OnVesselRecovered);
            GameEvents.onVesselTerminated.Add(OnVesselTerminated);
            GameEvents.onDockingComplete.Add(OnDockingComplete);
            GameEvents.onVesselsUndocking.Add(OnVesselsUndocking);
        }

        public void UnregisterEvents()
        {
            if (!_eventsRegistered)
                return;

            _eventsRegistered = false;
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onDockingComplete.Remove(OnDockingComplete);
            GameEvents.onVesselsUndocking.Remove(OnVesselsUndocking);
        }

        public void Reset()
        {
            StopSpectate();
            _tracked.Clear();
            _removed.Clear();
            _reconciled = false;
            _activeVesselId = Guid.Empty;
            _previousActiveVesselId = Guid.Empty;
        }

        public void Handle(IMessage message)
        {
            switch (message)
            {
                case VesselProtoMessage proto:
                    OnProto(proto);
                    break;
                case VesselUpdateMessage update:
                    OnUpdate(update);
                    break;
                case VesselRemoveMessage remove:
                    OnRemove(remove);
                    break;
                case VesselControlMessage control:
                    OnControl(control);
                    break;
                case VesselOwnerMessage owner:
                    OnOwner(owner);
                    break;
            }
        }

        public void Update()
        {
            double now = LocalClock.Now;
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != _scene)
                OnSceneChanged(scene, now);

            SuppressSpaceObjects();
            if (!IsLiveScene() || now - _sceneEnteredAt < SceneSettleSeconds)
                return;

            if (!_reconciled)
            {
                Reconcile();
                _reconciled = true;
                _nextScan = 0;
            }

            if (now >= _nextScan)
            {
                _nextScan = now + ScanIntervalSeconds;
                RemoveSpaceObjects();
                PublishLocalVessels(now);
                TakeOverNearbyVessels();
                ReleaseLeftVessels();
                SpawnRemoteVessels(now);
            }

            CheckSpectate();
            GuardTrackingStationButtons();
            TrackActiveVessel();
            SendLocalUpdates(now);
            PositionPuppets();
        }

        /// <summary>Se deja de mirar si la nave desaparece, si cambiamos de escena o si pasa a ser nuestra.</summary>
        private void CheckSpectate()
        {
            if (SpectatingId == Guid.Empty)
                return;

            TrackedVessel tracked = Find(SpectatingId);
            if (tracked == null || IsLocal(tracked) || LiveVessel(tracked) == null || HighLogic.LoadedScene != GameScenes.FLIGHT)
                StopSpectate();
        }

        private static bool IsLiveScene()
        {
            if (!ClientScenes.TimeFlows || FlightGlobals.fetch == null)
                return false;
            return HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.ready;
        }

        private static bool IsSyncable(Vessel vessel) =>
            vessel != null
            && vessel.state != Vessel.State.DEAD
            && vessel.id != Guid.Empty
            && vessel.vesselType != VesselType.SpaceObject
            && vessel.vesselType != VesselType.Unknown;

        /// <summary>Fuera del vuelo KSP destruye las naves empaquetadas que vuelan dentro de la atmósfera.</summary>
        private static bool CanExistInScene(VesselUpdateMessage update)
        {
            if (update == null || update.IsOnSurface || HighLogic.LoadedScene == GameScenes.FLIGHT)
                return true;

            CelestialBody body = VesselState.BodyOf(update);
            return body == null || body.GetPressure(update.Altitude) <= 1.0;
        }

        /// <summary>Cada jugador generaría asteroides distintos: en multijugador no se generan.</summary>
        private static void SuppressSpaceObjects()
        {
            ScenarioDiscoverableObjects spawner = ScenarioDiscoverableObjects.Instance;
            if (spawner == null || spawner.spawnOddsAgainst == int.MaxValue)
                return;

            spawner.spawnOddsAgainst = int.MaxValue;
            spawner.spawnInterval = float.MaxValue;
        }

        /// <summary>KSP genera algunos asteroides y cometas al crear la partida, antes de poder impedirlo: solo existirían para este jugador.</summary>
        private void RemoveSpaceObjects()
        {
            foreach (Vessel vessel in FlightGlobals.Vessels.ToList())
            {
                if (vessel == null || vessel.vesselType != VesselType.SpaceObject || vessel == FlightGlobals.ActiveVessel)
                    continue;

                ClientLog.Info($"Asteroide local eliminado: {vessel.vesselName}");
                RemoveSilently(vessel);
            }
        }

        private static Vessel LiveVessel(TrackedVessel tracked)
        {
            Vessel vessel = tracked.Vessel;
            if (vessel == null || vessel.state == Vessel.State.DEAD)
            {
                vessel = FlightGlobals.fetch != null ? FlightGlobals.FindVessel(tracked.Id) : null;
                tracked.Vessel = vessel;
            }

            return vessel != null && vessel.state != Vessel.State.DEAD ? vessel : null;
        }

        private bool IsLocal(TrackedVessel tracked) => tracked.ControllerId != 0 && tracked.ControllerId == _localPlayerId();

        private TrackedVessel GetOrTrack(Guid id)
        {
            if (!_tracked.TryGetValue(id, out TrackedVessel tracked))
            {
                tracked = new TrackedVessel(id);
                _tracked.Add(id, tracked);
            }

            return tracked;
        }

        private void OnSceneChanged(GameScenes scene, double now)
        {
            // Al salir de una escena con naves vivas KSP guarda la partida con las versiones que había cargadas;
            // al entrar en la siguiente las reconstruye desde ese guardado.
            foreach (TrackedVessel tracked in _tracked.Values)
            {
                if (_reconciled)
                    tracked.SavedVersion = tracked.SpawnedVersion;
                tracked.SpawnedVersion = -1;
                tracked.Vessel = null;
            }

            _scene = scene;
            _sceneEnteredAt = now;
            _reconciled = false;
            _activeVesselId = Guid.Empty;
            _previousActiveVesselId = Guid.Empty;
        }

        private void OnProto(VesselProtoMessage message)
        {
            if (message.VesselId == Guid.Empty || message.Data == null)
                return;

            _removed.Remove(message.VesselId);
            TrackedVessel tracked = GetOrTrack(message.VesselId);
            tracked.Name = message.VesselName ?? string.Empty;
            tracked.OwnerName = message.OwnerName ?? string.Empty;
            tracked.Access = message.Access;
            SetController(tracked, message.ControllerId);
            if (IsLocal(tracked))
                return;

            // Un reenvío periódico con la misma estructura solo actualiza los datos: recargar haría parpadear la nave.
            bool structureChanged = tracked.Proto == null || message.StructureVersion != tracked.RemoteStructureVersion;
            tracked.Proto = message.Data;
            tracked.RemoteStructureVersion = message.StructureVersion;
            if (!structureChanged)
                return;

            tracked.ProtoVersion++;
            tracked.NextSpawnAttempt = 0;
        }

        private void OnUpdate(VesselUpdateMessage message)
        {
            if (!_tracked.TryGetValue(message.VesselId, out TrackedVessel tracked) || IsLocal(tracked))
                return;

            // UDP puede desordenar: se descartan estados anteriores al actual.
            if (tracked.LastUpdate != null && message.UniversalTime < tracked.LastUpdate.UniversalTime)
                return;

            tracked.LastUpdate = message;
            if (tracked.SpawnedVersion < 0 || !IsLiveScene())
                return;

            Vessel vessel = LiveVessel(tracked);
            if (vessel != null && (vessel != FlightGlobals.ActiveVessel || IsSpectating(vessel)))
                VesselState.Apply(vessel, message);
        }

        private void OnRemove(VesselRemoveMessage message)
        {
            _removed.Add(message.VesselId);
            if (!_tracked.TryGetValue(message.VesselId, out TrackedVessel tracked))
                return;

            _tracked.Remove(message.VesselId);
            if (!IsLiveScene())
                return;

            Vessel vessel = LiveVessel(tracked);
            if (vessel != null)
                RemoveSilently(vessel);
        }

        private void OnControl(VesselControlMessage message)
        {
            if (_tracked.TryGetValue(message.VesselId, out TrackedVessel tracked))
                SetController(tracked, message.ControllerId);
        }

        private void OnOwner(VesselOwnerMessage message)
        {
            if (!_tracked.TryGetValue(message.VesselId, out TrackedVessel tracked))
                return;

            tracked.OwnerName = message.OwnerName ?? string.Empty;
            tracked.Access = message.Access;
        }

        private void SetController(TrackedVessel tracked, int controllerId)
        {
            bool wasLocal = IsLocal(tracked);
            bool wasPending = tracked.ControlPending;
            tracked.ControllerId = controllerId;
            tracked.ControlPending = false;
            bool isLocal = IsLocal(tracked);
            if (!IsLiveScene())
                return;

            Vessel vessel = LiveVessel(tracked);
            if (vessel == null)
                return;

            if (isLocal && !wasLocal)
            {
                VesselState.ReleasePuppet(vessel);
                tracked.ForgetPublished();
                tracked.NextUpdate = 0;
            }
            else if (!isLocal && wasLocal)
            {
                Adopt(tracked, vessel);
            }

            // Pilotando una nave que no controlamos: la pilota otro, se nos denegó o se nos retiró el permiso.
            // Mirándola (sin mandos) no pasa nada: para eso está el modo de mirar.
            if (!isLocal && vessel == FlightGlobals.ActiveVessel && !IsSpectating(vessel) && (controllerId != 0 || wasLocal || wasPending))
                LeaveRemoteVessel(tracked);
        }

        /// <summary>Alinea las naves que KSP acaba de reconstruir desde el guardado con lo que se sabe del servidor.</summary>
        private void Reconcile()
        {
            foreach (Vessel vessel in FlightGlobals.Vessels.ToList())
            {
                if (!IsSyncable(vessel))
                    continue;

                if (_removed.Contains(vessel.id))
                {
                    RemoveSilently(vessel);
                    continue;
                }

                if (!_tracked.TryGetValue(vessel.id, out TrackedVessel tracked) || IsLocal(tracked))
                    continue;

                if (tracked.Proto != null && tracked.SavedVersion == tracked.ProtoVersion)
                    Adopt(tracked, vessel);
                else if (vessel != FlightGlobals.ActiveVessel)
                    RemoveSilently(vessel); // Copia desfasada: se recreará desde la definición actual.
            }
        }

        private void Adopt(TrackedVessel tracked, Vessel vessel)
        {
            tracked.Vessel = vessel;
            tracked.SpawnedVersion = tracked.ProtoVersion;

            // La nave que se pilota nunca es marioneta: TrackActiveVessel decide si se toma el control o se abandona.
            // La que solo se mira sí lo es: la mueve el estado que llega por la red, como cualquier otra ajena.
            if (vessel == FlightGlobals.ActiveVessel && !IsSpectating(vessel))
                return;

            VesselState.MakePuppet(vessel);
            if (tracked.LastUpdate != null)
                VesselState.Apply(vessel, tracked.LastUpdate);
        }

        private void PublishLocalVessels(double now)
        {
            int localPlayerId = _localPlayerId();
            foreach (Vessel vessel in FlightGlobals.Vessels.ToList())
            {
                if (!IsSyncable(vessel) || _removed.Contains(vessel.id))
                    continue;

                if (!_tracked.TryGetValue(vessel.id, out TrackedVessel tracked))
                {
                    // Nave nueva en esta partida (lanzamiento, separación de etapas, EVA, bandera): se publica y es nuestra.
                    tracked = GetOrTrack(vessel.id);
                    tracked.ControllerId = localPlayerId;
                    tracked.OwnerName = _localPlayerName();
                    tracked.Access = _defaultAccess();
                    tracked.Vessel = vessel;
                    Publish(tracked, vessel, now);
                    continue;
                }

                if (!IsLocal(tracked))
                    continue;

                tracked.Vessel = vessel;
                if (vessel.loaded && (tracked.StructureChanged(vessel) || now >= tracked.NextProtoRefresh))
                    Publish(tracked, vessel, now);
            }
        }

        private void Publish(TrackedVessel tracked, Vessel vessel, double now)
        {
            tracked.NextProtoRefresh = now + ProtoRefreshSeconds;
            byte[] data = TryEncode(vessel);
            if (data == null)
                return;

            // Solo un cambio de estructura hace que los demás recarguen la nave; el refresco periódico no.
            if (tracked.StructureChanged(vessel))
                tracked.StructureVersion++;

            tracked.Name = Localizer.Format(vessel.vesselName);
            tracked.RememberPublished(vessel);
            tracked.NextUpdate = 0;
            SendProto(tracked, data);
        }

        private void SendProto(TrackedVessel tracked, byte[] data)
        {
            tracked.Proto = data;
            _send(
                new VesselProtoMessage
                {
                    VesselId = tracked.Id,
                    OwnerName = tracked.OwnerName,
                    Access = tracked.Access,
                    VesselName = tracked.Name,
                    StructureVersion = tracked.StructureVersion,
                    Data = data,
                },
                Delivery.Reliable);
        }

        private static byte[] TryEncode(Vessel vessel)
        {
            try
            {
                return VesselCodec.Encode(vessel);
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo serializar la nave {vessel.vesselName}: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Naves libres que se acercan a la nuestra: se pide su control para que KSP las simule aquí de verdad.
        /// Es lo que permite acoplarse, chocar o hacer EVA entre naves de jugadores distintos: mientras son
        /// marionetas están empaquetadas y no tienen física, así que ni los puertos de acoplamiento se tocan.
        /// </summary>
        private void TakeOverNearbyVessels()
        {
            // Mirando una nave ajena, la "nave activa" no es nuestra: no tiene sentido tomar nada a su alrededor.
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || SpectatingId != Guid.Empty)
                return;

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                return;

            Vector3d here = active.GetWorldPos3D();
            foreach (TrackedVessel tracked in _tracked.Values)
            {
                if (IsLocal(tracked) || tracked.ControllerId != 0 || tracked.ControlPending || !CanPilot(tracked))
                    continue;

                Vessel vessel = LiveVessel(tracked);
                if (vessel == null || vessel == active || Vector3d.Distance(here, vessel.GetWorldPos3D()) > TakeoverDistance)
                    continue;

                tracked.ControlPending = true;
                _send(new VesselControlRequestMessage { VesselId = tracked.Id, Acquire = true }, Delivery.Reliable);
                ClientLog.Info($"Nave cercana tomada para simularla aquí: {tracked.Name}");
            }
        }

        /// <summary>
        /// Las naves que ya no se pilotan ni se simulan aquí se sueltan para que otro jugador pueda tomarlas (si el acceso lo permite).
        /// Se quedan las que siguen cargadas cerca de la nave activa o van en vuelo (su física corre en esta partida).
        /// </summary>
        private void ReleaseLeftVessels()
        {
            Vessel active = HighLogic.LoadedScene == GameScenes.FLIGHT ? FlightGlobals.ActiveVessel : null;
            _scratch.Clear();
            _scratch.AddRange(_tracked.Values);
            foreach (TrackedVessel tracked in _scratch)
            {
                if (!IsLocal(tracked))
                    continue;

                Vessel vessel = LiveVessel(tracked);
                if (vessel == null || vessel == active || vessel.loaded || !IsStable(vessel.situation))
                    continue;

                Release(tracked, vessel);
            }
        }

        private static bool IsStable(Vessel.Situations situation)
        {
            switch (situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                case Vessel.Situations.ORBITING:
                case Vessel.Situations.ESCAPING:
                    return true;
                default:
                    return false;
            }
        }

        private void Release(TrackedVessel tracked, Vessel vessel)
        {
            // Todo por TCP y en orden: el servidor solo acepta la definición y el estado mientras la nave aún es nuestra.
            // La definición va con la misma versión de estructura (combustible, tripulación...: nadie la recarga).
            byte[] data = TryEncode(vessel);
            if (data != null)
                SendProto(tracked, data);

            VesselUpdateMessage last = VesselState.Capture(vessel, Planetarium.GetUniversalTime());
            _send(last, Delivery.Reliable);
            _send(new VesselControlRequestMessage { VesselId = tracked.Id, Acquire = false }, Delivery.Reliable);

            // Sin esperar la respuesta, para no volver a publicarla mientras tanto (el servidor nos la devolvería).
            tracked.LastUpdate = last;
            tracked.RemoteStructureVersion = tracked.StructureVersion;
            SetController(tracked, 0);
            ClientLog.Info($"Nave soltada: {tracked.Name}");
        }

        /// <summary>
        /// En la estación de seguimiento se desactivan "Volar" (si otro la pilota o es privada) y "Recuperar"/"Borrar"
        /// (si no hay permiso): así no se entra en una nave para que el mod te saque, ni se borra una nave ajena.
        /// KSP solo actualiza los botones al seleccionar, así que se comprueba cada frame.
        /// </summary>
        private void GuardTrackingStationButtons()
        {
            SpaceTracking tracking = HighLogic.LoadedScene == GameScenes.TRACKSTATION ? SpaceTracking.Instance : null;
            if (tracking == null || tracking.FlyButton == null)
            {
                _guardedVessel = Guid.Empty;
                _guardBlockedButtons = false;
                return;
            }

            Vessel selected = tracking.SelectedVessel;
            TrackedVessel tracked = selected != null ? Find(selected.id) : null;
            bool blockFly = tracked != null && !IsLocal(tracked) && !CanPilot(tracked);
            bool blockRemove = tracked != null && !CanRemove(tracked);
            Guid selectedId = selected != null ? selected.id : Guid.Empty;

            if (blockFly)
                tracking.FlyButton.interactable = false;
            if (blockRemove)
            {
                if (tracking.RecoverButton != null)
                    tracking.RecoverButton.interactable = false;
                if (tracking.DeleteButton != null)
                    tracking.DeleteButton.interactable = false;
            }

            bool blocked = blockFly || blockRemove;
            if (blocked && selectedId != _guardedVessel)
            {
                string reason = blockFly ? DescribeCannotPilot(tracked) : $"Solo {tracked.OwnerName} puede recuperar o borrar \"{tracked.Name}\"";
                ScreenMessages.PostScreenMessage(reason.Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);
            }
            else if (!blocked && _guardBlockedButtons && selectedId == _guardedVessel && selected != null)
            {
                // Se levantó el bloqueo sin cambiar de selección: KSP recalcula sus botones (sin reactivar lo que no toca).
                tracking.SetVessel(selected, true);
            }

            _guardedVessel = selectedId;
            _guardBlockedButtons = blocked;
        }

        private string DescribeCannotPilot(TrackedVessel tracked)
        {
            if (IsPilotedByOther(tracked))
                return $"\"{tracked.Name}\" la está pilotando {_playerName(tracked.ControllerId)}";
            if (!VesselPermissions.CanPilot(tracked.OwnerName, tracked.Access, _localPlayerName()))
                return $"\"{tracked.Name}\" es privada: solo {tracked.OwnerName} puede pilotarla";
            return $"No se pudo tomar el control de \"{tracked.Name}\"";
        }

        private void SpawnRemoteVessels(double now)
        {
            _scratch.Clear();
            _scratch.AddRange(_tracked.Values);
            foreach (TrackedVessel tracked in _scratch)
            {
                if (IsLocal(tracked)
                    || tracked.Proto == null
                    || tracked.SpawnedVersion == tracked.ProtoVersion
                    || tracked.FailedVersion == tracked.ProtoVersion
                    || now < tracked.NextSpawnAttempt)
                    continue;

                Vessel existing = FlightGlobals.FindVessel(tracked.Id);
                if (existing != null)
                {
                    if (existing == FlightGlobals.ActiveVessel)
                        continue; // No se recarga la nave que se está pilotando.
                    RemoveSilently(existing);
                }

                if (!CanExistInScene(tracked.LastUpdate))
                {
                    tracked.NextSpawnAttempt = now + RespawnRetrySeconds;
                    continue;
                }

                Vessel vessel = null;
                string error;
                try
                {
                    vessel = VesselSpawner.Spawn(VesselCodec.Decode(tracked.Proto), out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    ClientLog.Error($"Error al cargar la nave {tracked.Name}: {ex}");
                }

                if (vessel == null)
                {
                    tracked.FailedVersion = tracked.ProtoVersion;
                    string owner = VesselPermissions.HasOwner(tracked.OwnerName) ? $" de {tracked.OwnerName}" : string.Empty;
                    string notice = $"No se pudo cargar la nave \"{tracked.Name}\"{owner}: {error}";
                    ClientLog.Warn(notice);
                    ScreenMessages.PostScreenMessage(notice.Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);
                    continue;
                }

                Adopt(tracked, vessel);

                // La lista de la estación de seguimiento solo se reconstruye con este evento
                // (KSP lo lanza igual al generar naves de contratos).
                if (HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                    GameEvents.onNewVesselCreated.Fire(vessel);

                ClientLog.Info($"Nave remota cargada: {tracked.Name}");
            }
        }

        private void TrackActiveVessel()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                return;

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.id == _activeVesselId)
                return;

            _previousActiveVesselId = _activeVesselId;
            _activeVesselId = active.id;
            if (IsSpectating(active))
                return;

            if (!_tracked.TryGetValue(active.id, out TrackedVessel tracked) || IsLocal(tracked))
                return;

            if (CanPilot(tracked))
            {
                // Nadie la pilota y el acceso lo permite: se pide el control. Si se deniega, SetController nos saca de ella.
                VesselState.ReleasePuppet(active);
                tracked.ControlPending = true;
                _send(new VesselControlRequestMessage { VesselId = active.id, Acquire = true }, Delivery.Reliable);
            }
            else
            {
                LeaveRemoteVessel(tracked);
            }
        }

        private void LeaveRemoteVessel(TrackedVessel tracked)
        {
            string text = DescribeCannotPilot(tracked);
            ScreenMessages.PostScreenMessage(text.Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);
            ReturnToOwnVessel();
        }

        private void ReturnToOwnVessel()
        {
            Vessel previous = _previousActiveVesselId == Guid.Empty ? null : FlightGlobals.FindVessel(_previousActiveVesselId);
            if (previous != null && previous.loaded && FlightGlobals.SetActiveVessel(previous))
                return;

            // Sin una nave propia cercana a la que volver (p. ej. "Volar" desde la estación de seguimiento).
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }

        private void SendLocalUpdates(double now)
        {
            double universalTime = Planetarium.GetUniversalTime();
            foreach (TrackedVessel tracked in _tracked.Values)
            {
                if (!IsLocal(tracked) || now < tracked.NextUpdate)
                    continue;

                Vessel vessel = LiveVessel(tracked);
                if (vessel == null)
                    continue;

                bool physics = vessel.loaded && !vessel.packed;
                tracked.NextUpdate = now + (physics ? PhysicsUpdateIntervalSeconds : RailsUpdateIntervalSeconds);
                Delivery delivery = tracked.FirstUpdatePending ? Delivery.Reliable : Delivery.Unreliable;
                tracked.FirstUpdatePending = false;
                _send(VesselState.Capture(vessel, universalTime), delivery);
            }
        }

        private void PositionPuppets()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            foreach (TrackedVessel tracked in _tracked.Values)
            {
                if (IsLocal(tracked) || tracked.LastUpdate == null || tracked.SpawnedVersion != tracked.ProtoVersion)
                    continue;

                Vessel vessel = LiveVessel(tracked);
                if (vessel == null || (vessel == active && !IsSpectating(vessel)))
                    continue;

                if (!vessel.packed)
                    vessel.GoOnRails();
                VesselState.PositionPuppet(vessel, tracked.LastUpdate);
            }
        }

        private void RemoveSilently(Vessel vessel)
        {
            _suppressDestroyEvents++;
            try
            {
                VesselSpawner.Remove(vessel);
            }
            finally
            {
                _suppressDestroyEvents--;
            }
        }

        private void OnVesselWillDestroy(Vessel vessel)
        {
            if (_suppressDestroyEvents > 0 || vessel == null || !_tracked.TryGetValue(vessel.id, out TrackedVessel tracked))
                return;

            if (IsLocal(tracked))
            {
                // Destruida en nuestra partida: choque, acoplada a otra nave propia, kerbal que embarca...
                ForgetAndPublishRemoval(tracked);
                return;
            }

            // KSP destruyó una marioneta por sus reglas locales: se recreará cuando sea posible.
            tracked.Vessel = null;
            tracked.SpawnedVersion = -1;
            tracked.NextSpawnAttempt = LocalClock.Now + RespawnRetrySeconds;
        }

        /// <summary>Al acoplar, KSP funde las dos naves en una: se publica cuanto antes para que los demás la vean bien.</summary>
        private void OnDockingComplete(GameEvents.FromToAction<Part, Part> action)
        {
            _nextScan = 0;
            Vessel merged = action.to != null ? action.to.vessel : null;
            string name = merged != null ? merged.vesselName : "la nave";
            ClientLog.Info($"Acoplamiento completado: {name}");
            ScreenMessages.PostScreenMessage($"Acoplado: ahora {name} es una sola nave".Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Al desacoplar aparece una nave nueva: se publica enseguida para que no tarde en salir en los demás.</summary>
        private void OnVesselsUndocking(Vessel from, Vessel to) => _nextScan = 0;

        private void OnVesselRecovered(ProtoVessel proto, bool quick) => OnVesselGone(proto);

        private void OnVesselTerminated(ProtoVessel proto) => OnVesselGone(proto);

        private void OnVesselGone(ProtoVessel proto)
        {
            if (proto == null || !_tracked.TryGetValue(proto.vesselID, out TrackedVessel tracked))
                return;

            if (CanRemove(tracked))
            {
                ForgetAndPublishRemoval(tracked);
                return;
            }

            // Sin permiso (la pilota otro o no es nuestra): el servidor la mantiene, así que vuelve a aparecer.
            tracked.Vessel = null;
            tracked.SpawnedVersion = -1;
            ScreenMessages.PostScreenMessage($"\"{tracked.Name}\" no es tuya: volverá a aparecer".Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);
        }

        private void ForgetAndPublishRemoval(TrackedVessel tracked)
        {
            _tracked.Remove(tracked.Id);
            _removed.Add(tracked.Id);
            _send(new VesselRemoveMessage { VesselId = tracked.Id }, Delivery.Reliable);
        }
    }
}
