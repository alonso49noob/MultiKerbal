using System;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Progress;

namespace MultiKerbal.Client.Systems
{
    /// <summary>
    /// Progreso compartido (base): lo que cambia aquí (fondos, ciencia, reputación, tecnologías) se manda al servidor
    /// como diferencia y lo que manda el servidor (el total de todos) se aplica a la partida local.
    /// <list type="bullet">
    /// <item>Mientras se aplica el total del servidor no se mandan los avisos que KSP lanza por ello (si no, rebotaría).</item>
    /// <item>KSP vuelve a crear Funding, ResearchAndDevelopment y Reputation en cada escena: el total se reaplica al cambiar.</item>
    /// </list>
    /// Todavía no se comparten los experimentos ya hechos (su ciencia decreciente), los contratos, las piezas compradas
    /// ni las mejoras de edificios.
    /// </summary>
    internal sealed class ProgressSync
    {
        private readonly Action<IMessage> _send;
        private ProgressStateMessage _latest;
        private object _appliedTo;
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
            Mode = GameModes.IsValid(mode) ? mode : GameMode.Sandbox;
            _latest = null;
            _appliedTo = null;
            if (Mode != GameMode.Sandbox)
                Register();
        }

        public void Reset()
        {
            Unregister();
            Mode = GameMode.Sandbox;
            _latest = null;
            _appliedTo = null;
        }

        public void Handle(ProgressStateMessage state)
        {
            _latest = state;
            _appliedTo = null; // Hay que aplicarlo aunque la escena sea la misma.
        }

        /// <summary>Cada frame: aplica el último total si hay algo nuevo o la escena ha recreado los sistemas de KSP.</summary>
        public void Update()
        {
            if (Mode == GameMode.Sandbox || _latest == null)
                return;

            object current = CurrentScenario();
            if (current == null || ReferenceEquals(current, _appliedTo))
                return;

            Apply(_latest);
            _appliedTo = current;
        }

        /// <summary>El sistema de KSP que siempre existe en este modo (ciencia en los dos con progreso).</summary>
        private static object CurrentScenario() => ResearchAndDevelopment.Instance;

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
                    bool newTech = false;
                    foreach (string techId in state.Techs)
                    {
                        ProtoTechNode node = research.GetTechState(techId);
                        if (node != null && node.state == RDTech.State.Available)
                            continue;

                        research.SetTechState(techId, new ProtoTechNode { techID = techId, state = RDTech.State.Available });
                        newTech = true;
                    }

                    if (newTech)
                        ResearchAndDevelopment.RefreshTechTreeUI();
                }
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

        private void Register()
        {
            if (_registered)
                return;

            _registered = true;
            GameEvents.OnFundsChanged.Add(OnFundsChanged);
            GameEvents.OnScienceChanged.Add(OnScienceChanged);
            GameEvents.OnReputationChanged.Add(OnReputationChanged);
            GameEvents.OnTechnologyResearched.Add(OnTechnologyResearched);
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
        }

        /// <summary>Antes de recibir el total del servidor no se manda nada: no se sabría desde qué valor contar.</summary>
        private bool Ready => !_applying && _appliedTo != null;

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

            _send(new ProgressChangeMessage { Kind = ProgressKind.Tech, TechId = action.host.techID });
            ClientLog.Info($"Tecnología investigada: {action.host.techID}");
        }

        private void SendChange(ProgressKind kind, double delta)
        {
            if (Math.Abs(delta) < 1e-6)
                return;

            _send(new ProgressChangeMessage { Kind = kind, Amount = delta });
        }
    }
}
