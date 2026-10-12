using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Contracts;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Progress;
using Upgradeables;

namespace MultiKerbal.Client.Systems
{
    /// <summary>
    /// Progreso compartido: lo que cambia aquí se manda al servidor y lo que manda el servidor (lo de todos) se aplica
    /// a la partida local.
    /// <list type="bullet">
    /// <item>Fondos, ciencia y reputación van como diferencias; tecnologías, piezas compradas y edificios, como lo
    /// conseguido (solo avanzan).</item>
    /// <item>Experimentos: lo ya investigado de cada uno, para que repetirlo dé menos aunque lo hiciera otro.</item>
    /// <item>Contratos (carrera): las ofertas son de cada uno; uno aceptado es de todos, y el primero que lo termina
    /// cobra (a los demás se les quita sin pagar otra vez).</item>
    /// <item>Mientras se aplica lo del servidor no se mandan los avisos que KSP lanza por ello (si no, rebotaría).</item>
    /// <item>KSP vuelve a crear Funding, ResearchAndDevelopment, Reputation y ContractSystem en cada escena: el total
    /// se reaplica al cambiar, y lo que llega sin ellos espera.</item>
    /// </list>
    /// </summary>
    internal sealed class ProgressSync
    {
        private static readonly FieldInfo ScienceSubjectsField =
            typeof(ResearchAndDevelopment).GetField("scienceSubjects", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo LoadContractMethod =
            typeof(ContractSystem).GetMethod("LoadContract", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        private readonly Action<IMessage> _send;
        private readonly Dictionary<string, ScienceSubjectInfo> _subjects = new Dictionary<string, ScienceSubjectInfo>();
        private readonly Dictionary<Guid, ContractInfo> _contracts = new Dictionary<Guid, ContractInfo>();
        private ProgressStateMessage _latest;
        private object _appliedTo;
        private object _seenScene;

        /// <summary>Ya se aplicó una vez el total del servidor: desde ahí se cuentan las diferencias.</summary>
        private bool _initialized;
        private bool _subjectsPending;
        private bool _contractsPending;
        private bool _applying;
        private bool _registered;
        private double _funds;
        private float _science;
        private float _reputation;

        public ProgressSync(Action<IMessage> send)
        {
            _send = send;
        }

        public GameMode Mode { get; private set; } = GameMode.Sandbox;

        public void Start(GameMode mode)
        {
            Reset();
            Mode = GameModes.IsValid(mode) ? mode : GameMode.Sandbox;
            if (Mode != GameMode.Sandbox)
                Register();
        }

        public void Reset()
        {
            Unregister();
            Mode = GameMode.Sandbox;
            _latest = null;
            _appliedTo = null;
            _seenScene = null;
            _initialized = false;
            _subjects.Clear();
            _contracts.Clear();
            _subjectsPending = false;
            _contractsPending = false;
        }

        public void Handle(IMessage message)
        {
            switch (message)
            {
                case ProgressStateMessage state:
                    _latest = state;
                    _appliedTo = null; // Hay que aplicarlo aunque la escena sea la misma.
                    break;
                case ScienceSubjectsMessage subjects:
                    foreach (ScienceSubjectInfo subject in subjects.Subjects)
                    {
                        if (!_subjects.TryGetValue(subject.Id, out ScienceSubjectInfo known) || known.Science < subject.Science)
                            _subjects[subject.Id] = subject;
                    }

                    _subjectsPending = true;
                    break;
                case ContractsMessage contracts:
                    foreach (ContractInfo contract in contracts.Contracts)
                        _contracts[contract.Id] = contract;
                    _contractsPending = true;
                    break;
            }
        }

        /// <summary>Cada frame: aplica lo que haya llegado, o todo otra vez si la escena ha recreado los sistemas de KSP.</summary>
        public void Update()
        {
            if (Mode == GameMode.Sandbox)
                return;

            ResearchAndDevelopment research = ResearchAndDevelopment.Instance;
            if (research == null)
                return;

            bool newScene = !ReferenceEquals(research, _seenScene);
            _seenScene = research;
            if (_latest != null && !ReferenceEquals(research, _appliedTo))
            {
                Apply(_latest);
                _appliedTo = research;
                _initialized = true;
            }

            if (_subjectsPending || (newScene && _subjects.Count > 0))
                _subjectsPending = !ApplySubjects();

            if (GameModes.SharesMoney(Mode) && (_contractsPending || (newScene && _contracts.Count > 0)))
                _contractsPending = !ApplyContracts();
        }

        private void Apply(ProgressStateMessage state)
        {
            _applying = true;
            try
            {
                if (GameModes.SharesMoney(Mode) && Funding.Instance != null)
                {
                    Funding.Instance.SetFunds(state.Funds, TransactionReasons.None);
                    _funds = Funding.Instance.Funds;
                }

                if (GameModes.SharesMoney(Mode) && Reputation.Instance != null)
                {
                    Reputation.Instance.SetReputation((float)state.Reputation, TransactionReasons.None);
                    _reputation = Reputation.Instance.reputation;
                }

                ResearchAndDevelopment research = ResearchAndDevelopment.Instance;
                if (GameModes.SharesScience(Mode) && research != null)
                {
                    research.SetScience((float)state.Science, TransactionReasons.None);
                    _science = research.Science;
                    bool changed = ApplyTechs(research, state.Techs);
                    if (GameModes.SharesMoney(Mode))
                        changed |= ApplyParts(research, state.Parts);
                    if (changed)
                        ResearchAndDevelopment.RefreshTechTreeUI();
                }

                if (GameModes.SharesMoney(Mode))
                    ApplyFacilities(state.Facilities);
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo aplicar el progreso compartido: {ex.Message}");
            }
            finally
            {
                _applying = false;
            }
        }

        private static bool ApplyTechs(ResearchAndDevelopment research, string[] techs)
        {
            bool changed = false;
            foreach (string techId in techs)
            {
                ProtoTechNode node = research.GetTechState(techId);
                if (node != null && node.state == RDTech.State.Available)
                    continue;

                research.SetTechState(techId, new ProtoTechNode { techID = techId, state = RDTech.State.Available });
                changed = true;
            }

            return changed;
        }

        /// <summary>Piezas compradas en carrera: se añaden a su tecnología (si aún no está investigada aquí, se espera).</summary>
        private static bool ApplyParts(ResearchAndDevelopment research, string[] parts)
        {
            bool changed = false;
            foreach (string name in parts)
            {
                AvailablePart part = PartLoader.getPartInfoByName(name);
                if (part == null || ResearchAndDevelopment.PartModelPurchased(part))
                    continue;

                ProtoTechNode tech = research.GetTechState(part.TechRequired);
                if (tech == null || tech.state != RDTech.State.Available)
                    continue;

                if (tech.partsPurchased == null)
                    tech.partsPurchased = new List<AvailablePart>();
                tech.partsPurchased.Add(part);
                research.SetTechState(part.TechRequired, tech);
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Edificios: si están en la escena se mejoran ellos; si no, se cambia el nivel guardado, que KSP usará al
        /// cargarlos. Solo se sube de nivel.
        /// </summary>
        private static void ApplyFacilities(FacilityLevel[] facilities)
        {
            if (ScenarioUpgradeableFacilities.protoUpgradeables == null)
                return;

            foreach (FacilityLevel facility in facilities)
            {
                if (!ScenarioUpgradeableFacilities.protoUpgradeables.TryGetValue(facility.Id, out ScenarioUpgradeableFacilities.ProtoUpgradeable proto))
                    continue;

                if (facility.Level <= proto.GetLevel() + 1e-4f)
                    continue;

                if (proto.facilityRefs != null && proto.facilityRefs.Count > 0)
                {
                    foreach (UpgradeableFacility building in proto.facilityRefs)
                        building.setNormLevel(facility.Level);
                }

                proto.configNode?.SetValue("lvl", facility.Level.ToString(CultureInfo.InvariantCulture), true);
                ClientLog.Info($"Edificio mejorado por otro jugador: {facility.Id} ({facility.Level:0.##})");
            }
        }

        /// <summary>
        /// Experimentos: se crean los que no hay y se sube la ciencia conseguida de los que hay. KSP no tiene forma
        /// pública de añadirlos, así que se meten en su lista interna. Devuelve false si aún no se puede (sin R&amp;D).
        /// </summary>
        private bool ApplySubjects()
        {
            ResearchAndDevelopment research = ResearchAndDevelopment.Instance;
            if (research == null || !GameModes.SharesScience(Mode))
                return false;

            if (!(ScienceSubjectsField?.GetValue(research) is Dictionary<string, ScienceSubject> subjects))
            {
                ClientLog.Warn("No se encontró la lista de experimentos de KSP: no se comparten");
                return true;
            }

            foreach (ScienceSubjectInfo info in _subjects.Values)
            {
                if (!subjects.TryGetValue(info.Id, out ScienceSubject subject))
                {
                    subject = new ScienceSubject(info.Id, info.Title, info.DataScale, info.SubjectValue, info.ScienceCap);
                    subjects[info.Id] = subject;
                }

                if (subject.science >= info.Science)
                    continue;

                subject.science = info.Science;
                subject.scientificValue = Math.Min(subject.scientificValue, info.ScientificValue);
            }

            return true;
        }

        /// <summary>
        /// Contratos de los demás: los aceptados se añaden activos (como hace KSP al cargar la partida) y los terminados
        /// se quitan de los activos sin pagar: la recompensa ya llegó con los fondos de quien lo terminó.
        /// Devuelve false si aún no se puede (sin sistema de contratos en esta escena).
        /// </summary>
        private bool ApplyContracts()
        {
            ContractSystem system = ContractSystem.Instance;
            if (system == null || !ContractSystem.loaded || LoadContractMethod == null)
                return false;

            bool changed = false;
            _applying = true;
            try
            {
                foreach (ContractInfo info in _contracts.Values)
                    changed |= ApplyContract(system, info);
            }
            finally
            {
                _applying = false;
            }

            if (changed)
                GameEvents.Contract.onContractsListChanged.Fire();
            return true;
        }

        private static bool ApplyContract(ContractSystem system, ContractInfo info)
        {
            try
            {
                // GetContractByGuid solo busca entre los activos.
                Contract known = system.GetContractByGuid(info.Id);
                bool finishedHere = system.ContractsFinished.Exists(c => c.ContractGuid == info.Id);
                bool finished = info.Status != ContractStatus.Active;
                if (!finished)
                {
                    if (known != null || finishedHere)
                        return false;

                    Contract added = LoadContract(system, info.Node);
                    if (added == null)
                        return false;

                    system.Contracts.Add(added);
                    if (added.ContractState == Contract.State.Active)
                        added.Register();
                    ClientLog.Info($"Contrato aceptado por otro jugador: {added.Title}");
                    return true;
                }

                if (finishedHere)
                    return false;

                if (known != null)
                {
                    known.Unregister();
                    system.Contracts.Remove(known);
                }

                Contract done = LoadContract(system, info.Node);
                if (done != null)
                    system.ContractsFinished.Add(done);
                ClientLog.Info($"Contrato terminado por otro jugador ({info.Status}): {done?.Title ?? info.Id.ToString()}");
                return true;
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo aplicar el contrato {info.Id}: {ex.Message}");
                return false;
            }
        }

        private static Contract LoadContract(ContractSystem system, string text)
        {
            ConfigNode parsed = ConfigNode.Parse(text);
            ConfigNode node = parsed?.GetNode("CONTRACT") ?? parsed;
            if (node == null)
                return null;

            object target = LoadContractMethod.IsStatic ? null : system;
            return LoadContractMethod.Invoke(target, new object[] { node }) as Contract;
        }

        private void Register()
        {
            if (_registered)
                return;

            _registered = true;
            GameEvents.OnFundsChanged.Add(OnFundsChanged);
            GameEvents.OnScienceChanged.Add(OnScienceChanged);
            GameEvents.OnReputationChanged.Add(OnReputationChanged);
            GameEvents.OnTechnologyResearched.Add(OnTechnologyResearched);
            GameEvents.OnScienceRecieved.Add(OnScienceReceived);
            GameEvents.OnPartPurchased.Add(OnPartPurchased);
            GameEvents.OnKSCFacilityUpgraded.Add(OnFacilityUpgraded);
            GameEvents.Contract.onAccepted.Add(OnContractChanged);
            GameEvents.Contract.onCompleted.Add(OnContractChanged);
            GameEvents.Contract.onFailed.Add(OnContractChanged);
            GameEvents.Contract.onCancelled.Add(OnContractChanged);
        }

        private void Unregister()
        {
            if (!_registered)
                return;

            _registered = false;
            GameEvents.OnFundsChanged.Remove(OnFundsChanged);
            GameEvents.OnScienceChanged.Remove(OnScienceChanged);
            GameEvents.OnReputationChanged.Remove(OnReputationChanged);
            GameEvents.OnTechnologyResearched.Remove(OnTechnologyResearched);
            GameEvents.OnScienceRecieved.Remove(OnScienceReceived);
            GameEvents.OnPartPurchased.Remove(OnPartPurchased);
            GameEvents.OnKSCFacilityUpgraded.Remove(OnFacilityUpgraded);
            GameEvents.Contract.onAccepted.Remove(OnContractChanged);
            GameEvents.Contract.onCompleted.Remove(OnContractChanged);
            GameEvents.Contract.onFailed.Remove(OnContractChanged);
            GameEvents.Contract.onCancelled.Remove(OnContractChanged);
        }

        /// <summary>Antes de recibir el total del servidor no se manda nada: no se sabría desde qué valor contar.</summary>
        private bool Ready => !_applying && _initialized;

        private void OnFundsChanged(double funds, TransactionReasons reason)
        {
            double delta = funds - _funds;
            _funds = funds;
            if (Ready && GameModes.SharesMoney(Mode))
                SendChange(ProgressKind.Funds, delta);
        }

        private void OnScienceChanged(float science, TransactionReasons reason)
        {
            double delta = (double)science - _science;
            _science = science;
            if (Ready && GameModes.SharesScience(Mode))
                SendChange(ProgressKind.Science, delta);
        }

        private void OnReputationChanged(float reputation, TransactionReasons reason)
        {
            double delta = (double)reputation - _reputation;
            _reputation = reputation;
            if (Ready && GameModes.SharesMoney(Mode))
                SendChange(ProgressKind.Reputation, delta);
        }

        private void OnTechnologyResearched(GameEvents.HostTargetAction<RDTech, RDTech.OperationResult> action)
        {
            if (!Ready || !GameModes.SharesScience(Mode) || action.target != RDTech.OperationResult.Successful || action.host == null)
                return;

            _send(new ProgressChangeMessage { Kind = ProgressKind.Tech, Id = action.host.techID });
            ClientLog.Info($"Tecnología investigada: {action.host.techID}");
        }

        /// <summary>Se ha recibido ciencia de un experimento: se manda cómo ha quedado (lo ya investigado de él).</summary>
        private void OnScienceReceived(float amount, ScienceSubject subject, ProtoVessel vessel, bool reverseEngineered)
        {
            if (_applying || !GameModes.SharesScience(Mode) || subject == null || string.IsNullOrEmpty(subject.id))
                return;

            var info = new ScienceSubjectInfo
            {
                Id = subject.id,
                Title = subject.title ?? string.Empty,
                DataScale = subject.dataScale,
                SubjectValue = subject.subjectValue,
                ScienceCap = subject.scienceCap,
                Science = subject.science,
                ScientificValue = subject.scientificValue,
            };
            _subjects[info.Id] = info;
            _send(new ScienceSubjectsMessage { Subjects = new[] { info } });
        }

        private void OnPartPurchased(AvailablePart part)
        {
            if (Ready && GameModes.SharesMoney(Mode) && part != null)
                _send(new ProgressChangeMessage { Kind = ProgressKind.Part, Id = part.name });
        }

        private void OnFacilityUpgraded(UpgradeableFacility facility, int level)
        {
            if (!Ready || !GameModes.SharesMoney(Mode) || facility == null || string.IsNullOrEmpty(facility.id))
                return;

            // La clave con la que KSP guarda el edificio (con "SpaceCenter/" delante si no lo lleva).
            string id = ScenarioUpgradeableFacilities.SlashSanitize(facility.id);
            _send(new ProgressChangeMessage { Kind = ProgressKind.Facility, Id = id, Amount = facility.GetNormLevel() });
            ClientLog.Info($"Edificio mejorado: {facility.id} (nivel {level})");
        }

        /// <summary>Un contrato aceptado o terminado aquí: se manda con su definición para que lo tengan los demás.</summary>
        private void OnContractChanged(Contract contract)
        {
            if (_applying || !GameModes.SharesMoney(Mode) || contract == null || !TryStatusOf(contract.ContractState, out ContractStatus status))
                return;

            try
            {
                var node = new ConfigNode("CONTRACT");
                contract.Save(node);
                var info = new ContractInfo { Id = contract.ContractGuid, Status = status, Node = node.ToString() };
                _contracts[info.Id] = info;
                _send(new ContractsMessage { Contracts = new[] { info } });
                ClientLog.Info($"Contrato {status}: {contract.Title}");
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo compartir el contrato {contract.Title}: {ex.Message}");
            }
        }

        private static bool TryStatusOf(Contract.State state, out ContractStatus status)
        {
            switch (state)
            {
                case Contract.State.Active:
                    status = ContractStatus.Active;
                    return true;
                case Contract.State.Completed:
                    status = ContractStatus.Completed;
                    return true;
                case Contract.State.DeadlineExpired:
                    status = ContractStatus.DeadlineExpired;
                    return true;
                case Contract.State.Failed:
                    status = ContractStatus.Failed;
                    return true;
                case Contract.State.Cancelled:
                    status = ContractStatus.Cancelled;
                    return true;
                default:
                    status = default;
                    return false;
            }
        }

        private void SendChange(ProgressKind kind, double delta)
        {
            if (Math.Abs(delta) < 1e-6)
                return;

            _send(new ProgressChangeMessage { Kind = kind, Amount = delta });
        }
    }
}
