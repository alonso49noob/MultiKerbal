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
        /// <summary>
        /// 30 Hz. KSP simula la física a 50 Hz, así que 60 repetiría estados y doblaría el tráfico; los saltos entre
        /// estados los quita <see cref="PuppetMotion"/> en quien los recibe.
        /// </summary>
        private const double PhysicsUpdateIntervalSeconds = 1.0 / 30.0;
        private const double RailsUpdateIntervalSeconds = 5.0;
        private const double ProtoRefreshSeconds = 30.0;
        private const double RespawnRetrySeconds = 10.0;
        private const float NoticeSeconds = 5f;
        private const double MergeMemorySeconds = 2.0;
        private const double AwaitLevelTimeoutSeconds = 60.0;
        private const double SeparationMemorySeconds = 10.0;

        /// <summary>Lo que se espera a que KSP cargue la versión nueva de la nave mirada antes de recargar el vuelo.</summary>
        private const double SwapTimeoutSeconds = 5.0;

        /// <summary>KSP carga las naves a 2,5 km: el control se pide algo antes para que llegue a tiempo.</summary>
        private const double TakeoverDistance = 2200.0;

        private const string SpectateLockId = "MultiKerbalSpectate";

        /// <summary>Mandos de la nave y cambio de nave ([ ] o el mapa): para salir está "Dejar de mirar".</summary>
        private const ControlTypes SpectateLocks =
            ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.STAGING | ControlTypes.CUSTOM_ACTION_GROUPS | ControlTypes.ACTIONS_SHIP
            | ControlTypes.VESSEL_SWITCHING;

        private readonly Func<int> _localPlayerId;
        private readonly Func<string> _localPlayerName;
        private readonly Func<int, string> _playerName;
        private readonly Func<VesselAccess> _defaultAccess;
        private readonly Action<IMessage, Delivery> _send;
        private readonly Dictionary<Guid, TrackedVessel> _tracked = new Dictionary<Guid, TrackedVessel>();
        private readonly HashSet<Guid> _removed = new HashSet<Guid>();

        /// <summary>Naves que acaban de fundirse con otra (hasta cuándo se recuerda): su destrucción no es un choque.</summary>
        private readonly Dictionary<Guid, double> _merging = new Dictionary<Guid, double>();

        /// <summary>De qué nave acaba de salir cada una (hasta publicarla): ver <see cref="RememberSeparation"/>.</summary>
        private readonly Dictionary<Guid, Separation> _parents = new Dictionary<Guid, Separation>();

        /// <summary>Cambio en curso de la nave mirada por su versión nueva (ver <see cref="BeginSwap"/>).</summary>
        private SpectateSwap _swap;
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

        /// <summary>Adónde volver al dejar de mirar: la nave que pilotábamos (vacío si ninguna) o, si no, esta escena.</summary>
        private Guid _returnVesselId;
        private GameScenes _returnScene = GameScenes.SPACECENTER;

        /// <summary>La última nave que se miró: nunca se "vuelve" a ella.</summary>
        private Guid _lastSpectatedId;

        /// <summary>Se pulsó "Dejar de mirar" mientras cargaba el vuelo: se hace al terminar.</summary>
        private bool _stopWhenLoaded;

        /// <summary>Se pidió otra escena y KSP aún no ha terminado de cargarla.</summary>
        private bool _awaitingLevel;
        private double _loadRequestedAt;

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
            !IsPilotedByOther(tracked) && HasPilotPermission(tracked);

        /// <summary>Destruirla, recuperarla o borrarla: hace falta permiso del dueño aunque la pilotemos.</summary>
        public bool CanRemove(TrackedVessel tracked) =>
            (IsLocal(tracked) || tracked.ControllerId == 0) && HasRemovePermission(tracked);

        public bool CanChangeOwnership(TrackedVessel tracked) =>
            VesselPermissions.CanChangeOwnership(tracked.OwnerName, _localPlayerName());

        /// <summary>Nos la han prestado.</summary>
        public bool IsLentToMe(TrackedVessel tracked) => VesselPermissions.IsBorrower(tracked.LentTo, _localPlayerName());

        /// <summary>
        /// El dueño la presta a un jugador conectado o la recupera (nombre vacío); quien la tiene prestada la devuelve
        /// (nombre vacío). El servidor decide y avisa a todos.
        /// </summary>
        public void RequestLend(TrackedVessel tracked, string playerName) =>
            _send(new VesselLendRequestMessage { VesselId = tracked.Id, PlayerName = playerName ?? string.Empty }, Delivery.Reliable);

        /// <summary>El dueño la devuelve a como la dejó (si se la han devuelto dañada). El servidor decide y avisa a todos.</summary>
        public void RequestRestore(TrackedVessel tracked) =>
            _send(new VesselRestoreRequestMessage { VesselId = tracked.Id }, Delivery.Reliable);

        /// <summary>El kerbal va en una nave ajena (la pilota otro o nadie): lo que le pase aquí no cuenta.</summary>
        public bool IsCrewOfRemoteVessel(string kerbal)
        {
            foreach (TrackedVessel tracked in _tracked.Values)
            {
                if (!IsLocal(tracked) && Array.Exists(tracked.Crew, name => string.Equals(name, kerbal, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        private bool HasPilotPermission(TrackedVessel tracked) =>
            VesselPermissions.CanPilot(tracked.OwnerName, tracked.Access, tracked.LentTo, _localPlayerName());

        private bool HasRemovePermission(TrackedVessel tracked) =>
            VesselPermissions.CanRemove(tracked.OwnerName, tracked.Access, _localPlayerName());

        /// <summary>Nave que estamos mirando sin pilotarla (vacío si ninguna).</summary>
        public Guid SpectatingId { get; private set; }

        public bool IsSpectating(Vessel vessel) => vessel != null && SpectatingId != Guid.Empty && vessel.id == SpectatingId;

        /// <summary>
        /// Mirar una nave ajena, con los mandos bloqueados, desde cualquier sitio. Si está cargada cerca basta con
        /// cambiar la cámara; si no, se carga el vuelo alrededor de ella, como "Volar" en la estación de seguimiento.
        /// Al dejar de mirar se vuelve a la nave o a la escena de antes.
        /// </summary>
        public bool BeginSpectate(TrackedVessel tracked, out string error)
        {
            if (IsLocal(tracked))
            {
                error = Loc.T("Esa nave ya la pilotas tú", "You are already flying that vessel");
                return false;
            }

            Vessel vessel = LiveVessel(tracked);
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && vessel != null && vessel.loaded)
            {
                bool wasSpectating = SpectatingId != Guid.Empty;
                if (!wasSpectating)
                    RememberWhereToReturn();

                // Mirando ya, la nave activa es una copia que no se simula aquí: no hace falta que KSP compruebe si se puede cambiar.
                bool switched = vessel == FlightGlobals.ActiveVessel
                                || (wasSpectating ? FlightGlobals.ForceSetActiveVessel(vessel) : FlightGlobals.SetActiveVessel(vessel));
                if (switched)
                {
                    SpectatingId = tracked.Id;
                    InputLockManager.SetControlLock(SpectateLocks, SpectateLockId);
                    error = null;
                    ClientLog.Info($"Mirando \"{tracked.Name}\"");
                    return true;
                }

                if (!wasSpectating)
                    ForgetReturn();
                error = Loc.T("KSP no ha dejado cambiar la cámara a esa nave", "KSP would not move the camera to that vessel");
                return false;
            }

            return FlyToSpectate(tracked, false, out error);
        }

        /// <summary>Deja de mirar y vuelve a la nave o la escena de antes (o al Centro Espacial si ya no está).</summary>
        public void StopSpectate()
        {
            if (SpectatingId == Guid.Empty)
                return;

            // Aún cargando el vuelo alrededor de la nave: se deja para cuando termine (si no, se quedaría pilotando la copia).
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && !_reconciled)
            {
                _stopWhenLoaded = true;
                return;
            }

            Guid spectated = SpectatingId;
            ClearSpectate();

            // Si mientras mirábamos nos dieron el control, nos quedamos en ella (y deja de ser marioneta).
            Vessel active = FlightGlobals.ActiveVessel;
            bool nowMine = _tracked.TryGetValue(spectated, out TrackedVessel tracked) && IsLocal(tracked);
            if (active != null && active.id == spectated && nowMine)
                VesselState.ReleasePuppet(active);

            if (active != null && active.id == spectated && !nowMine)
                ReturnToOwnVessel();
            else
                ForgetReturn();
        }

        private void ClearSpectate()
        {
            if (SpectatingId != Guid.Empty)
                _lastSpectatedId = SpectatingId;
            SpectatingId = Guid.Empty;
            _stopWhenLoaded = false;
            InputLockManager.RemoveControlLock(SpectateLockId);
        }

        /// <summary>Antes de empezar a mirar: la nave que pilotábamos (si estábamos volando) y la escena.</summary>
        private void RememberWhereToReturn()
        {
            Vessel active = HighLogic.LoadedScene == GameScenes.FLIGHT ? FlightGlobals.ActiveVessel : null;
            _returnVesselId = active != null ? active.id : Guid.Empty;
            _returnScene = HighLogic.LoadedScene == GameScenes.TRACKSTATION ? GameScenes.TRACKSTATION : GameScenes.SPACECENTER;
        }

        private void ForgetReturn()
        {
            _returnVesselId = Guid.Empty;
            _returnScene = GameScenes.SPACECENTER;
        }

        /// <summary>
        /// Una nave a la que se puede volver: propia (o que se puede pilotar) y que no sea la que se estaba mirando
        /// (volver a ella sería tomarla sin pedirlo, o rebotar entre dos naves ajenas).
        /// </summary>
        private bool CanReturnTo(Guid id)
        {
            if (id == Guid.Empty || id == SpectatingId || id == _lastSpectatedId)
                return false;

            TrackedVessel tracked = Find(id);
            return tracked == null || IsLocal(tracked) || CanPilot(tracked);
        }

        /// <summary>
        /// La nave que pilotamos se quedará sin cargar: tiene que poder quedarse sola. Posada y quieta, o en órbita.
        /// Una suborbital se estrellaría sobre raíles (y se borraría para todos); en la atmósfera KSP la destruye.
        /// </summary>
        private static bool CanBeLeftAlone(Vessel vessel)
        {
            switch (vessel.situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                    return vessel.srfSpeed < 1.0;
                case Vessel.Situations.ORBITING:
                case Vessel.Situations.ESCAPING:
                case Vessel.Situations.DOCKED:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Carga el vuelo alrededor de una nave ajena para mirarla. Se parte de la partida actual con la nave puesta al día
        /// (definición y último estado), porque puede que aquí ni exista: fuera del vuelo no se crean las que vuelan en la atmósfera.
        /// </summary>
        /// <param name="reload">Ya se la está mirando y ha cambiado su estructura (no se vuelve a comprobar la nave propia).</param>
        private bool FlyToSpectate(TrackedVessel tracked, bool reload, out string error)
        {
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT && scene != GameScenes.SPACECENTER && scene != GameScenes.TRACKSTATION)
            {
                error = Loc.T("Sal del hangar para mirar otras naves", "Leave the editor to watch other vessels");
                return false;
            }

            if (!IsLiveScene() || !_reconciled)
            {
                error = Loc.T("Espera a que termine de cargar la escena", "Wait for the scene to finish loading");
                return false;
            }

            if (tracked.Proto == null || tracked.LastUpdate == null)
            {
                error = Loc.T("Aún no se sabe dónde está esa nave", "That vessel's position is not known yet");
                return false;
            }

            if (scene == GameScenes.FLIGHT && !reload && !CanLeaveOwnVessel())
            {
                error = Loc.T(
                    "Para mirar una nave lejana, deja antes la tuya estable: posada y quieta, o en órbita con los motores apagados",
                    "To watch a distant vessel, first leave yours stable: landed and still, or in orbit with the engines off");
                return false;
            }

            Game game = HighLogic.CurrentGame.Updated();
            ProtoVessel proto;
            try
            {
                proto = VesselSpawner.CreateProto(VesselCodec.Decode(tracked.Proto), game, out error);
            }
            catch (Exception ex)
            {
                ClientLog.Error($"No se pudo preparar la nave {tracked.Name} para mirarla: {ex}");
                error = ex.Message;
                proto = null;
            }

            if (proto == null)
            {
                error = Loc.T($"No se pudo cargar \"{tracked.Name}\": {error}", $"Could not load \"{tracked.Name}\": {error}");
                return false;
            }

            VesselState.PlaceProto(proto, tracked.LastUpdate);
            List<ProtoVessel> protos = game.flightState.protoVessels;
            int index = protos.FindIndex(p => p.vesselID == tracked.Id);
            if (index >= 0)
            {
                protos[index] = proto;
            }
            else
            {
                protos.Add(proto);
                index = protos.Count - 1;
            }

            if (SpectatingId == Guid.Empty)
                RememberWhereToReturn();

            // Así, al entrar en el vuelo, la copia que KSP crea desde la partida se adopta como marioneta en vez de recargarla.
            tracked.SpawnedVersion = tracked.ProtoVersion;
            tracked.SavedVersion = tracked.ProtoVersion;
            SpectatingId = tracked.Id;
            ClientLog.Info($"Cargando el vuelo para mirar \"{tracked.Name}\"");
            FlyTo(game, index);
            error = null;
            return true;
        }

        /// <summary>
        /// La nave propia a la que se volverá (la activa si aún no se mira nada) puede quedarse sin cargar. KSP pide además
        /// lo mismo que para guardar o cambiar de nave: nada de acelerar ni de estar a punto de estrellarse.
        /// </summary>
        private bool CanLeaveOwnVessel()
        {
            bool spectating = SpectatingId != Guid.Empty;
            Vessel own = spectating
                ? (_returnVesselId == Guid.Empty ? null : FlightGlobals.FindVessel(_returnVesselId))
                : FlightGlobals.ActiveVessel;
            if (own == null || !own.loaded)
                return true;

            if (!spectating && FlightGlobals.ClearToSave() != ClearToSaveStatus.CLEAR)
                return false;

            return CanBeLeftAlone(own);
        }

        private static void FlyTo(Game game, int vesselIndex)
        {
            GamePersistence.SaveGame(game, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel(game, vesselIndex);
        }

        /// <summary>
        /// La nave mirada no se simula aquí: la mueve la red. KSP saca de raíles a la nave activa sin mirar sus distancias
        /// de carga (en cada LateUpdate), así que se retiene en cada frame, desde el primero tras cargar el vuelo.
        /// Si nos la ceden mientras tanto, se deja de retener: entonces la simulamos nosotros.
        /// </summary>
        private void HoldSpectatedVessel()
        {
            if (SpectatingId == Guid.Empty || HighLogic.LoadedScene != GameScenes.FLIGHT || FlightGlobals.fetch == null)
                return;

            Vessel active = FlightGlobals.ActiveVessel;
            if (!IsSpectatedVessel(active))
                return;

            TrackedVessel tracked = Find(SpectatingId);
            if (tracked != null && IsLocal(tracked))
                return;

            InputLockManager.SetControlLock(SpectateLocks, SpectateLockId);
            OrbitPhysicsManager.HoldVesselUnpack(2);
            if (!_reconciled)
                VesselState.MakePuppet(active);
            else if (!active.packed && FlightGlobals.ready)
                active.GoOnRails();
        }

        /// <summary>
        /// Al pedir otra escena (también al recargar el vuelo desde el vuelo, que no cambia HighLogic.LoadedScene) se da
        /// por terminada la actual. Hasta que KSP termine de cargar la nueva no se toca nada.
        /// </summary>
        private void OnSceneLoadRequested(GameScenes scene)
        {
            double now = LocalClock.Now;
            OnSceneChanged(GameScenes.LOADING, now);
            _awaitingLevel = true;
            _loadRequestedAt = now;
        }

        /// <summary>La escena nueva ya está: el segundo de espera antes de reconciliar cuenta desde aquí.</summary>
        private void OnLevelWasLoaded(GameScenes scene)
        {
            _awaitingLevel = false;
            _sceneEnteredAt = LocalClock.Now;
        }

        /// <summary>Pide a quien la pilota que nos ceda el control (por ejemplo, para acoplarse).</summary>
        public void RequestHandover(TrackedVessel tracked)
        {
            _send(new VesselHandoverRequestMessage { VesselId = tracked.Id }, Delivery.Reliable);
            ScreenMessages.PostScreenMessage(
                Loc.T($"Pedido el control de \"{tracked.Name}\" a {_playerName(tracked.ControllerId)}",
                      $"Asked {_playerName(tracked.ControllerId)} to hand over \"{tracked.Name}\"").Replace("<", "‹"),
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
            GameEvents.onPartDeCoupleNewVesselComplete.Add(OnDecoupleNewVessel);
            GameEvents.onCrewOnEva.Add(OnCrewOnEva);
            GameEvents.onPartCouple.Add(OnPartCouple);
            GameEvents.onCrewBoardVessel.Add(OnPartCouple);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
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
            GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnDecoupleNewVessel);
            GameEvents.onCrewOnEva.Remove(OnCrewOnEva);
            GameEvents.onPartCouple.Remove(OnPartCouple);
            GameEvents.onCrewBoardVessel.Remove(OnPartCouple);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
        }

        public void Reset()
        {
            // Sin volver a ninguna nave: al desconectarse se va al menú principal.
            ClearSpectate();
            ForgetReturn();
            _lastSpectatedId = Guid.Empty;
            _tracked.Clear();
            _removed.Clear();
            _merging.Clear();
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
                case VesselRejectMessage reject:
                    OnReject(reject);
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
            HoldSpectatedVessel();

            // Si KSP no avisara de que terminó de cargar, no se queda parado para siempre.
            if (_awaitingLevel && now - _loadRequestedAt > AwaitLevelTimeoutSeconds)
                _awaitingLevel = false;
            if (_awaitingLevel || !IsLiveScene() || now - _sceneEnteredAt < SceneSettleSeconds)
                return;

            if (!_reconciled)
            {
                Reconcile();
                _reconciled = true;
                _nextScan = 0;
            }

            if (_stopWhenLoaded)
                StopSpectate();

            if (now >= _nextScan)
            {
                _nextScan = now + ScanIntervalSeconds;
                RemoveSpaceObjects();
                PublishLocalVessels(now);
                TakeOverNearbyVessels();
                ReleaseLeftVessels();
                SpawnRemoteVessels(now);
            }

            UpdateSwap(now);
            CheckSpectate();
            GuardTrackingStationButtons();
            TrackActiveVessel();
            SendLocalUpdates(now);
            PositionPuppets(now);
        }

        /// <summary>
        /// Se deja de mirar si la nave pasa a ser nuestra (nos quedamos en ella), si salimos del vuelo, o si la nave
        /// desaparece o KSP cambia de nave (entonces se vuelve a la propia, salvo que la nueva activa ya lo sea).
        /// </summary>
        private void CheckSpectate()
        {
            if (SpectatingId == Guid.Empty)
                return;

            TrackedVessel tracked = Find(SpectatingId);
            if (tracked != null && IsLocal(tracked))
            {
                StopSpectate();
                return;
            }

            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                ClearSpectate();
                ForgetReturn();
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            if (tracked != null && LiveVessel(tracked) != null && (active == null || IsSpectatedVessel(active)))
                return;

            ClearSpectate();
            TrackedVessel activeTracked = active != null ? Find(active.id) : null;
            bool activeIsOurs = active != null && active.id != _lastSpectatedId && (activeTracked == null || IsLocal(activeTracked));
            if (activeIsOurs)
                ForgetReturn();
            else
                ReturnToOwnVessel();
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

            _swap = null; // La escena se va: la copia nueva se va con ella.
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
            tracked.LentTo = message.LentTo ?? string.Empty;
            tracked.Crew = message.Crew ?? new string[0];
            tracked.Restorable = message.Restorable;
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

            // Con otra estructura se acepta cualquier estado que llegue después, aunque sea anterior al último: el servidor
            // puede haberla devuelto a como estaba antes (alguien la destruyó sin permiso del dueño).
            tracked.LastUpdate = null;
            tracked.Motion.Clear();
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
            tracked.LentTo = message.LentTo ?? string.Empty;
            tracked.Restorable = message.Restorable;
        }

        private void SetController(TrackedVessel tracked, int controllerId)
        {
            bool wasLocal = IsLocal(tracked);
            bool wasPending = tracked.ControlPending;
            tracked.ControllerId = controllerId;
            tracked.ControlPending = false;
            bool isLocal = IsLocal(tracked);
            if (isLocal)
                tracked.Motion.Clear();
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
                if (!IsSyncable(vessel) || _removed.Contains(vessel.id) || (_swap != null && vessel == _swap.Replacement))
                    continue;

                if (!_tracked.TryGetValue(vessel.id, out TrackedVessel tracked))
                {
                    // Nave nueva en esta partida (lanzamiento, separación de etapas, EVA, bandera): se publica y la
                    // pilotamos. Si salió de una nave ajena, el servidor la deja a nombre de su dueño.
                    tracked = GetOrTrack(vessel.id);
                    tracked.ControllerId = localPlayerId;
                    tracked.OwnerName = _localPlayerName();
                    tracked.Access = _defaultAccess();
                    tracked.Vessel = vessel;
                    tracked.ParentId = _parents.TryGetValue(vessel.id, out Separation separation) ? separation.ParentId : Guid.Empty;
                    _parents.Remove(vessel.id);
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
            SendProto(tracked, vessel, data);
        }

        private void SendProto(TrackedVessel tracked, Vessel vessel, byte[] data)
        {
            tracked.Proto = data;
            _send(
                new VesselProtoMessage
                {
                    VesselId = tracked.Id,
                    OwnerName = tracked.OwnerName,
                    Access = tracked.Access,
                    VesselName = tracked.Name,
                    Crew = VesselSpawner.CrewOf(vessel),
                    ParentId = tracked.ParentId,
                    StructureVersion = tracked.StructureVersion,
                    Data = data,
                },
                Delivery.Reliable);
            tracked.ParentId = Guid.Empty; // Solo cuenta al crearla.
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

                // Mientras se mira una nave, la propia se guarda para volver a ella (si no, otro podría llevársela).
                if (SpectatingId != Guid.Empty && vessel.id == _returnVesselId)
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
                SendProto(tracked, vessel, data);

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
                string reason = blockFly
                    ? DescribeCannotPilot(tracked)
                    : Loc.T($"Solo {tracked.OwnerName} puede recuperar o borrar \"{tracked.Name}\"",
                            $"Only {tracked.OwnerName} can recover or delete \"{tracked.Name}\"");
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
                return Loc.T($"\"{tracked.Name}\" la está pilotando {_playerName(tracked.ControllerId)}",
                             $"{_playerName(tracked.ControllerId)} is flying \"{tracked.Name}\"");
            if (!HasPilotPermission(tracked))
                return Loc.T($"\"{tracked.Name}\" es privada: solo {tracked.OwnerName} puede pilotarla",
                             $"\"{tracked.Name}\" is private: only {tracked.OwnerName} can fly it");
            return Loc.T($"No se pudo tomar el control de \"{tracked.Name}\"",
                         $"Could not take control of \"{tracked.Name}\"");
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
                    {
                        // La nave activa no se puede quitar de la escena. Si es la que miramos y ha cambiado (etapas,
                        // acoplamiento...), se cambia por su versión nueva sin recargar; si no se puede, se recarga el
                        // vuelo alrededor de ella (y ya no se sigue: la escena está cambiando). La que pilotamos no se recarga.
                        if (IsSpectating(existing))
                        {
                            if (_swap != null || BeginSwap(tracked))
                                continue; // Se está cambiando por la versión nueva (ver UpdateSwap).

                            ReloadSpectated(tracked);
                            return;
                        }

                        continue;
                    }

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
                    string owner = VesselPermissions.HasOwner(tracked.OwnerName)
                        ? Loc.T($" de {tracked.OwnerName}", $" from {tracked.OwnerName}")
                        : string.Empty;
                    string notice = Loc.T($"No se pudo cargar la nave \"{tracked.Name}\"{owner}: {error}",
                                          $"Could not load the vessel \"{tracked.Name}\"{owner}: {error}");
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

        /// <summary>La nave que se mira, o la copia nueva que la está sustituyendo.</summary>
        private bool IsSpectatedVessel(Vessel vessel) =>
            vessel != null && (IsSpectating(vessel) || (_swap != null && vessel == _swap.Replacement));

        /// <summary>
        /// La nave mirada ha cambiado (soltó una etapa, se acopló...) sin recargar el vuelo: se crea su versión nueva al
        /// lado, con otro identificador para que no choque con la vieja; cuando KSP la carga se le pasa la cámara, se quita
        /// la vieja y la nueva toma el identificador de verdad (<see cref="UpdateSwap"/>). Si algo falla, se recarga el vuelo.
        /// </summary>
        private bool BeginSwap(TrackedVessel tracked)
        {
            try
            {
                DecodedVessel decoded = VesselCodec.Decode(tracked.Proto);
                Guid temporaryId = Guid.NewGuid();
                decoded.VesselNode.SetValue("pid", temporaryId.ToString("N"), true);
                Vessel replacement = VesselSpawner.Spawn(decoded, out string error, besideItsCopy: true);
                if (replacement == null)
                {
                    ClientLog.Warn($"No se pudo crear la versión nueva de \"{tracked.Name}\": {error}");
                    return false;
                }

                VesselState.MakePuppet(replacement);
                if (tracked.LastUpdate != null)
                    VesselState.Apply(replacement, tracked.LastUpdate);

                _swap = new SpectateSwap(tracked, replacement, tracked.ProtoVersion, LocalClock.Now + SwapTimeoutSeconds);
                ClientLog.Info($"\"{tracked.Name}\" ha cambiado: cambiando la copia mirada sin recargar el vuelo");
                return true;
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo preparar la versión nueva de \"{tracked.Name}\": {ex.Message}");
                return false;
            }
        }

        private void UpdateSwap(double now)
        {
            SpectateSwap swap = _swap;
            if (swap == null)
                return;

            TrackedVessel tracked = swap.Tracked;
            Vessel replacement = swap.Replacement;
            bool stillWatching = SpectatingId == tracked.Id && Find(tracked.Id) == tracked && !IsLocal(tracked);
            bool alive = replacement != null && replacement.state != Vessel.State.DEAD;
            if (!stillWatching || !alive || now > swap.Deadline)
            {
                AbortSwap(stillWatching && tracked.ProtoVersion == swap.Version);
                return;
            }

            // Si mientras tanto llegó otra versión, se empieza de nuevo con ella.
            if (tracked.ProtoVersion != swap.Version)
            {
                AbortSwap(false);
                return;
            }

            if (!replacement.loaded)
                return;

            Vessel old = FlightGlobals.ActiveVessel;
            if (old != replacement && !FlightGlobals.ForceSetActiveVessel(replacement))
            {
                AbortSwap(true);
                return;
            }

            if (FlightGlobals.ActiveVessel != replacement)
                return; // KSP cambia de nave en el siguiente frame: se termina entonces.

            _swap = null;
            Vessel previous = old != replacement ? old : FlightGlobals.FindVessel(tracked.Id);
            if (previous != null && previous != replacement && previous.id == tracked.Id)
                RemoveSilently(previous, crewMoved: true);

            replacement.id = tracked.Id;
            if (replacement.protoVessel != null)
                replacement.protoVessel.vesselID = tracked.Id;
            tracked.Vessel = replacement;
            tracked.SpawnedVersion = swap.Version;
            tracked.Motion.Clear();
            ClientLog.Info($"Copia mirada cambiada: {tracked.Name}");
        }

        /// <summary>Se quita la copia nueva y, si se sigue mirando la misma versión, se recarga el vuelo como antes.</summary>
        private void AbortSwap(bool reload)
        {
            SpectateSwap swap = _swap;
            _swap = null;
            if (swap.Replacement != null && swap.Replacement.state != Vessel.State.DEAD && swap.Replacement != FlightGlobals.ActiveVessel)
                RemoveSilently(swap.Replacement, crewMoved: true);
            if (reload)
                ReloadSpectated(swap.Tracked);
        }

        private void ReloadSpectated(TrackedVessel tracked)
        {
            ScreenMessages.PostScreenMessage(
                Loc.T($"\"{tracked.Name}\" ha cambiado: recargando la vista", $"\"{tracked.Name}\" has changed: reloading the view").Replace("<", "‹"),
                NoticeSeconds,
                ScreenMessageStyle.UPPER_CENTER);
            if (FlyToSpectate(tracked, true, out string error))
                return;

            // Sin poder recargarla se deja de mirar (la copia vieja ya no se movería bien); no se reintenta en bucle.
            tracked.FailedVersion = tracked.ProtoVersion;
            ClientLog.Warn(error);
            Notice(error);
            StopSpectate();
        }

        /// <summary>
        /// A la nave que pilotábamos antes de mirar (o de entrar en una que no podemos pilotar), aunque esté lejos;
        /// si ya no existe, a la escena de la que veníamos.
        /// </summary>
        private void ReturnToOwnVessel()
        {
            Guid targetId = CanReturnTo(_returnVesselId) ? _returnVesselId
                : CanReturnTo(_previousActiveVesselId) ? _previousActiveVesselId
                : Guid.Empty;
            GameScenes scene = _returnScene;
            ForgetReturn();

            Vessel previous = targetId == Guid.Empty ? null : FlightGlobals.FindVessel(targetId);
            if (previous != null && previous != FlightGlobals.ActiveVessel)
            {
                // La nave activa es ajena y no la simulamos: KSP no tiene que comprobar si se puede cambiar (lo haría sobre ella).
                if (previous.loaded && FlightGlobals.ForceSetActiveVessel(previous))
                    return;

                Game game = HighLogic.CurrentGame.Updated();
                int index = game.flightState.protoVessels.FindIndex(p => p.vesselID == targetId);
                if (index >= 0)
                {
                    FlyTo(game, index);
                    return;
                }
            }

            // Sin una nave propia a la que volver (p. ej. "Volar" desde la estación de seguimiento).
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(scene);
        }

        private void SendLocalUpdates(double now)
        {
            double universalTime = Planetarium.GetUniversalTime();
            foreach (TrackedVessel tracked in _tracked.Values)
            {
                if (!IsLocal(tracked))
                    continue;

                Vessel vessel = LiveVessel(tracked);
                if (vessel == null)
                    continue;

                bool physics = vessel.loaded && !vessel.packed;
                double interval = physics ? PhysicsUpdateIntervalSeconds : RailsUpdateIntervalSeconds;

                // Al salir de raíles (fin del warp, nave que se carga) no se esperan los segundos que quedaban.
                if (tracked.NextUpdate > now + interval)
                    tracked.NextUpdate = now;
                if (now < tracked.NextUpdate)
                    continue;

                // Se cuenta desde el envío previsto y no desde ahora: si no, los frames redondearían 30 Hz a 20.
                double next = tracked.NextUpdate + interval;
                tracked.NextUpdate = next > now ? next : now + interval;
                Delivery delivery = tracked.FirstUpdatePending ? Delivery.Reliable : Delivery.Unreliable;
                tracked.FirstUpdatePending = false;
                _send(VesselState.Capture(vessel, universalTime), delivery);
            }
        }

        private void PositionPuppets(double now)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            double universalTime = Planetarium.GetUniversalTime();
            // Sobre raíles no se adelanta (0): ver PuppetMotion.
            double warpRate = TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1f ? 0.0 : TimeWarp.CurrentRate;
            foreach (TrackedVessel tracked in _tracked.Values)
            {
                if (IsLocal(tracked) || tracked.LastUpdate == null || tracked.SpawnedVersion != tracked.ProtoVersion)
                    continue;

                Vessel vessel = LiveVessel(tracked);
                if (vessel == null || (vessel == active && !IsSpectating(vessel)))
                    continue;

                VesselUpdateMessage update = tracked.LastUpdate;
                CelestialBody body = VesselState.BodyOf(update);
                if (body == null)
                    continue;

                if (update.UniversalTime > tracked.Motion.LatestUniversalTime)
                {
                    tracked.Motion.Add(
                        update.BodyIndex, update.UniversalTime, VesselState.PoseOf(update), universalTime, now, warpRate, body.Radius, update.IsOnSurface);
                }

                if (!vessel.packed)
                    vessel.GoOnRails();
                VesselState.PositionPuppet(vessel, update, tracked.Motion.Current(universalTime, now, warpRate));
            }
        }

        /// <param name="crewMoved">Su tripulación sigue en otra copia de la misma nave: no se toca su estado.</param>
        private void RemoveSilently(Vessel vessel, bool crewMoved = false)
        {
            _suppressDestroyEvents++;
            try
            {
                VesselSpawner.Remove(vessel, crewMoved);
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
                // Destruida en nuestra partida: choque, calor... o fundida con otra (acoplamiento, kerbal que embarca),
                // que no necesita permiso del dueño. Si no lo hay, el servidor la devuelve a donde la tomamos.
                VesselRemoveReason reason = WasMerged(vessel.id) ? VesselRemoveReason.Merged : VesselRemoveReason.Destroyed;
                if (reason == VesselRemoveReason.Destroyed && !HasRemovePermission(tracked))
                    Notice(Loc.T(
                        $"\"{tracked.Name}\" es de {tracked.OwnerName}: volverá a donde estaba cuando la tomaste",
                        $"\"{tracked.Name}\" belongs to {tracked.OwnerName}: it will go back where it was when you took it"));
                ForgetAndPublishRemoval(tracked, reason);
                return;
            }

            // KSP destruyó una marioneta por sus reglas locales: se recreará cuando sea posible.
            tracked.Vessel = null;
            tracked.SpawnedVersion = -1;
            tracked.NextSpawnAttempt = LocalClock.Now + RespawnRetrySeconds;
        }

        /// <summary>
        /// KSP avisa de que dos piezas se unen (acoplamiento, garra) o de que un kerbal embarca justo antes de destruir
        /// la nave que desaparece en la fusión: se apuntan las dos naves durante un momento.
        /// </summary>
        private void OnPartCouple(GameEvents.FromToAction<Part, Part> action)
        {
            RememberMerge(action.from);
            RememberMerge(action.to);
        }

        private void RememberMerge(Part part)
        {
            if (part != null && part.vessel != null)
                _merging[part.vessel.id] = LocalClock.Now + MergeMemorySeconds;
        }

        private bool WasMerged(Guid vesselId) =>
            _merging.TryGetValue(vesselId, out double until) && LocalClock.Now <= until;

        /// <summary>
        /// El servidor no acepta una nave que acabamos de publicar (por ejemplo, lleva un kerbal que va en la nave de
        /// otro jugador): se quita de esta partida. Si es la que pilotamos, se vuelve al Centro Espacial y se quita allí.
        /// </summary>
        private void OnReject(VesselRejectMessage message)
        {
            _removed.Add(message.VesselId);
            string name = Find(message.VesselId)?.Name ?? string.Empty;
            _tracked.Remove(message.VesselId);
            Notice(Loc.T($"No se puede usar \"{name}\": {message.Reason}", $"\"{name}\" cannot be used: {message.Reason}"));
            ClientLog.Warn($"Nave rechazada por el servidor: {name}: {message.Reason}");
            if (!IsLiveScene())
                return;

            Vessel vessel = FlightGlobals.FindVessel(message.VesselId);
            if (vessel == null)
                return;

            if (vessel != FlightGlobals.ActiveVessel)
            {
                RemoveSilently(vessel);
                return;
            }

            // La nave activa no se puede quitar: al entrar en el Centro Espacial, Reconcile la quita (está en _removed).
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }

        private static void Notice(string text) =>
            ScreenMessages.PostScreenMessage(text.Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);

        /// <summary>Al acoplar, KSP funde las dos naves en una: se publica cuanto antes para que los demás la vean bien.</summary>
        private void OnDockingComplete(GameEvents.FromToAction<Part, Part> action)
        {
            _nextScan = 0;
            Vessel merged = action.to != null ? action.to.vessel : null;
            string name = merged != null ? merged.vesselName : "la nave";
            ClientLog.Info($"Acoplamiento completado: {name}");
            ScreenMessages.PostScreenMessage(
                Loc.T($"Acoplado: ahora {name} es una sola nave", $"Docked: {name} is now a single vessel").Replace("<", "‹"),
                NoticeSeconds,
                ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Al desacoplar aparece una nave nueva: se publica enseguida para que no tarde en salir en los demás.</summary>
        private void OnVesselsUndocking(Vessel from, Vessel to)
        {
            _nextScan = 0;
            RememberSeparation(from, to);
        }

        /// <summary>
        /// Una nave que sale de otra (etapa, desacople, desacoplamiento, kerbal en EVA): se apunta de cuál salió para
        /// decírselo al servidor al publicarla. Se apuntan las dos direcciones porque KSP no siempre dice cuál es la nueva;
        /// solo se usa la de la que aún no se ha publicado.
        /// </summary>
        private void RememberSeparation(Vessel a, Vessel b)
        {
            if (a == null || b == null || a == b)
                return;

            double until = LocalClock.Now + SeparationMemorySeconds;
            _parents[b.id] = new Separation(a.id, until);
            _parents[a.id] = new Separation(b.id, until);
            foreach (Guid stale in _parents.Where(p => p.Value.Until < LocalClock.Now).Select(p => p.Key).ToList())
                _parents.Remove(stale);
        }

        private void OnDecoupleNewVessel(Vessel original, Vessel created) => RememberSeparation(original, created);

        private void OnCrewOnEva(GameEvents.FromToAction<Part, Part> action) =>
            RememberSeparation(action.from != null ? action.from.vessel : null, action.to != null ? action.to.vessel : null);

        private void OnVesselRecovered(ProtoVessel proto, bool quick) => OnVesselGone(proto, VesselRemoveReason.Recovered);

        private void OnVesselTerminated(ProtoVessel proto) => OnVesselGone(proto, VesselRemoveReason.Deleted);

        private void OnVesselGone(ProtoVessel proto, VesselRemoveReason reason)
        {
            if (proto == null || !_tracked.TryGetValue(proto.vesselID, out TrackedVessel tracked))
                return;

            if (CanRemove(tracked))
            {
                ForgetAndPublishRemoval(tracked, reason);
                return;
            }

            Notice(Loc.T(
                $"\"{tracked.Name}\" no es tuya: sin permiso de {tracked.OwnerName} no se puede recuperar ni borrar, así que volverá a aparecer",
                $"\"{tracked.Name}\" is not yours: without {tracked.OwnerName}'s permission it cannot be recovered or deleted, so it will come back"));
            if (IsLocal(tracked))
            {
                // La pilotábamos: el servidor nos quita el control y nos la devuelve tal como está.
                ForgetAndPublishRemoval(tracked, reason);
                return;
            }

            // La pilota otro o no tenemos permiso: el servidor la mantiene, así que vuelve a aparecer.
            tracked.Vessel = null;
            tracked.SpawnedVersion = -1;
        }

        private void ForgetAndPublishRemoval(TrackedVessel tracked, VesselRemoveReason reason)
        {
            _tracked.Remove(tracked.Id);
            _removed.Add(tracked.Id);
            _send(new VesselRemoveMessage { VesselId = tracked.Id, Reason = reason }, Delivery.Reliable);
        }

        /// <summary>De qué nave salió una y hasta cuándo se recuerda.</summary>
        private struct Separation
        {
            public Separation(Guid parentId, double until)
            {
                ParentId = parentId;
                Until = until;
            }

            public Guid ParentId { get; }

            public double Until { get; }
        }

        private sealed class SpectateSwap
        {
            public SpectateSwap(TrackedVessel tracked, Vessel replacement, int version, double deadline)
            {
                Tracked = tracked;
                Replacement = replacement;
                Version = version;
                Deadline = deadline;
            }

            public TrackedVessel Tracked { get; }

            /// <summary>La copia nueva, con un identificador temporal hasta que sustituye a la vieja.</summary>
            public Vessel Replacement { get; }

            /// <summary>Versión de la definición con la que se creó.</summary>
            public int Version { get; }

            public double Deadline { get; }
        }
    }
}
