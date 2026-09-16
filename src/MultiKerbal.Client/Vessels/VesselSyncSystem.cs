using System;
using System.Collections.Generic;
using System.Linq;
using KSP.Localization;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;

namespace MultiKerbal.Client.Vessels
{
    /// <summary>
    /// Mantiene las naves de la partida local alineadas con el servidor.
    /// <list type="bullet">
    /// <item>Naves propias: se publican al crearse o cambiar de estructura y se envía su estado periódicamente.</item>
    /// <item>Naves ajenas: marionetas empaquetadas que siguen el último estado recibido.</item>
    /// </list>
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

        private readonly Func<int> _localPlayerId;
        private readonly Func<int, string> _playerName;
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

        public VesselSyncSystem(Func<int> localPlayerId, Func<int, string> playerName, Action<IMessage, Delivery> send)
        {
            _localPlayerId = localPlayerId;
            _playerName = playerName;
            _send = send;
        }

        public int TotalVessels => _tracked.Count;

        public int OwnVessels
        {
            get
            {
                int count = 0;
                foreach (TrackedVessel tracked in _tracked.Values)
                {
                    if (IsLocal(tracked))
                        count++;
                }

                return count;
            }
        }

        public void RegisterEvents()
        {
            if (_eventsRegistered)
                return;

            _eventsRegistered = true;
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselRecovered.Add(OnVesselRecovered);
            GameEvents.onVesselTerminated.Add(OnVesselTerminated);
        }

        public void UnregisterEvents()
        {
            if (!_eventsRegistered)
                return;

            _eventsRegistered = false;
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
        }

        public void Reset()
        {
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
                case VesselOwnershipMessage ownership:
                    OnOwnership(ownership);
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
                PublishLocalVessels(now);
                SpawnRemoteVessels(now);
            }

            TrackActiveVessel();
            SendLocalUpdates(now);
            PositionPuppets();
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

        private bool IsLocal(TrackedVessel tracked) => tracked.OwnerId != 0 && tracked.OwnerId == _localPlayerId();

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
            SetOwner(tracked, message.OwnerId);
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
            if (vessel != null && vessel != FlightGlobals.ActiveVessel)
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

        private void OnOwnership(VesselOwnershipMessage message)
        {
            if (_tracked.TryGetValue(message.VesselId, out TrackedVessel tracked))
                SetOwner(tracked, message.OwnerId);
        }

        private void SetOwner(TrackedVessel tracked, int ownerId)
        {
            bool wasLocal = IsLocal(tracked);
            tracked.OwnerId = ownerId;
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

            // Pilotando una nave que controla otro jugador (petición denegada o perdida).
            if (ownerId != 0 && !isLocal && vessel == FlightGlobals.ActiveVessel)
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
            if (vessel == FlightGlobals.ActiveVessel)
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
                    tracked.OwnerId = localPlayerId;
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
            byte[] data;
            try
            {
                data = VesselCodec.Encode(vessel);
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo serializar la nave {vessel.vesselName}: {ex}");
                return;
            }

            // Solo un cambio de estructura hace que los demás recarguen la nave; el refresco periódico no.
            if (tracked.StructureChanged(vessel))
                tracked.StructureVersion++;

            tracked.Proto = data;
            tracked.Name = Localizer.Format(vessel.vesselName);
            tracked.RememberPublished(vessel);
            tracked.NextUpdate = 0;
            _send(
                new VesselProtoMessage
                {
                    VesselId = vessel.id,
                    VesselName = tracked.Name,
                    StructureVersion = tracked.StructureVersion,
                    Data = data,
                },
                Delivery.Reliable);
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
                    string notice = $"No se pudo cargar la nave \"{tracked.Name}\" de {_playerName(tracked.OwnerId)}: {error}";
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
            if (!_tracked.TryGetValue(active.id, out TrackedVessel tracked) || IsLocal(tracked))
                return;

            if (tracked.OwnerId == 0)
            {
                // Sin dueño: se pide el control. Si el servidor lo deniega, SetOwner nos saca de ella.
                VesselState.ReleasePuppet(active);
                _send(new VesselOwnershipRequestMessage { VesselId = active.id, Acquire = true }, Delivery.Reliable);
            }
            else
            {
                LeaveRemoteVessel(tracked);
            }
        }

        private void LeaveRemoteVessel(TrackedVessel tracked)
        {
            string text = $"\"{tracked.Name}\" la controla {_playerName(tracked.OwnerId)}";
            ScreenMessages.PostScreenMessage(text.Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);

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
                if (vessel == null || vessel == active)
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

        private void OnVesselRecovered(ProtoVessel proto, bool quick) => OnVesselGone(proto);

        private void OnVesselTerminated(ProtoVessel proto) => OnVesselGone(proto);

        private void OnVesselGone(ProtoVessel proto)
        {
            if (proto == null || !_tracked.TryGetValue(proto.vesselID, out TrackedVessel tracked))
                return;

            if (tracked.OwnerId == 0 || IsLocal(tracked))
            {
                ForgetAndPublishRemoval(tracked);
                return;
            }

            // Es de otro jugador: el servidor la mantiene, así que vuelve a aparecer.
            tracked.Vessel = null;
            tracked.SpawnedVersion = -1;
        }

        private void ForgetAndPublishRemoval(TrackedVessel tracked)
        {
            _tracked.Remove(tracked.Id);
            _removed.Add(tracked.Id);
            _send(new VesselRemoveMessage { VesselId = tracked.Id }, Delivery.Reliable);
        }
    }
}
