using System;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Time;

namespace MultiKerbal.Client.Systems
{
    /// <summary>
    /// Traduce el TimeWarp local a peticiones al servidor y aplica solo el warp que permite el consenso,
    /// más el de otros jugadores que se acepte según los ajustes de warp. Una petición que nadie acompaña caduca
    /// a los <see cref="RequestTimeoutSeconds"/> segundos, o antes con la tecla de bajar warp.
    /// </summary>
    internal sealed class WarpSystem
    {
        private const double ClampNoticeDelaySeconds = 0.75;
        private const double RequestTimeoutSeconds = 15.0;
        private const float NoticeSeconds = 4f;

        private readonly SharedClock _clock;
        private readonly Action<IMessage> _send;
        private readonly Func<string> _localPlayerName;
        private readonly Func<WarpPolicy> _policy;
        private readonly WarpDecision _decision = new WarpDecision();
        private readonly LadderCache _rails = new LadderCache();
        private readonly LadderCache _physics = new LadderCache();

        /// <summary>Situación neutra para escenas sin tiempo (menú, hangar): solo decide el rechazo sin condiciones.</summary>
        private static readonly WarpContext NeutralContext = new WarpContext
        {
            StableSituation = true,
            EnginesOff = true,
            RailsMode = true,
            MaxRailsRate = 1.0,
        };

        private bool _hasSent;
        private bool _sentParticipating;
        private double _sentRate;
        private WarpMode _sentMode;
        private double _sentAcceptUpTo;
        private bool _sentAutoDeny;
        private double _clampedSince = double.NaN;
        private bool _clampNoticeShown;
        private string _clampNoticeLimitedBy;
        private bool _limiterNoticeShown;
        private bool _acceptNoticeShown;

        public WarpSystem(SharedClock clock, Action<IMessage> send, Func<string> localPlayerName, Func<WarpPolicy> policy)
        {
            _clock = clock;
            _send = send;
            _localPlayerName = localPlayerName;
            _policy = policy;
        }

        /// <summary>Qué pasaría ahora mismo si otro jugador pidiera warp (para la ventana de ajustes).</summary>
        public string PolicyStatus { get; private set; } = string.Empty;

        public void Reset()
        {
            _decision.Reset();
            _hasSent = false;
            _limiterNoticeShown = false;
            _acceptNoticeShown = false;
            ResetClamp();
        }

        public void Update()
        {
            TimeWarp timeWarp = TimeWarp.fetch;
            WarpPolicy settings = _policy();
            if (!ClientScenes.TimeFlows || timeWarp == null || !_clock.HasState)
            {
                _decision.Reset();
                ResetClamp();

                // El rechazo automático sin condiciones vale también donde no corre el tiempo (hangar).
                WarpPolicyDecision outside = WarpPolicyEvaluator.Evaluate(settings, NeutralContext);
                bool deny = outside.AutoDeny && ClientScenes.IsGameplay;
                PolicyStatus = deny
                    ? $"Aquí también rechazas el warp de los demás ({outside.Reason})."
                    : "En esta escena no participas en el warp: no limitas a nadie.";
                Send(deny, 1.0, WarpMode.Rails, 1.0, deny);
                return;
            }

            WarpMode mode = timeWarp.Mode == TimeWarp.Modes.LOW ? WarpMode.Physics : WarpMode.Rails;
            double[] railsLadder = _rails.Get(timeWarp.warpRates);
            double[] ladder = mode == WarpMode.Physics ? _physics.Get(timeWarp.physicsWarpRates) : railsLadder;

            WarpPolicyDecision policy = WarpPolicyEvaluator.Evaluate(settings, WarpContextReader.Read(timeWarp, railsLadder));
            PolicyStatus = Describe(settings, policy);

            // Nadie pide warp: vuelve a aceptarse hasta el límite del ajuste aunque antes se bajara un warp aceptado.
            if (_clock.Rate <= 1.0 && string.IsNullOrEmpty(_clock.LimitedBy))
                _decision.ResetAcceptRefusal();

            int currentIndex = TimeWarp.CurrentRateIndex;
            int apply = _decision.Update(mode, currentIndex, ladder, Math.Max(1.0, _clock.Rate), _clock.Mode, policy.AcceptUpTo, out _);
            if (apply >= 0)
            {
                // Bajar al instante (si no, el tiempo local se adelanta); subir con la transición normal de KSP.
                bool lowering = apply < currentIndex;
                TimeWarp.SetRate(apply, lowering, !lowering);
            }

            double now = LocalClock.Now;
            UpdateClamp(now);
            UpdateAcceptNotice();
            UpdateLimiterNotice(policy);

            double acceptUpTo = policy.AutoDeny ? 1.0 : _decision.EffectiveAcceptUpTo(railsLadder, policy.AcceptUpTo);
            Send(true, _decision.DesiredRate(ladder), _decision.RequestedMode(ladder), acceptUpTo, policy.AutoDeny);
        }

        private static bool CancelKeyPressed() =>
            (GameSettings.TIME_WARP_DECREASE?.GetKeyDown() ?? false)
            || (GameSettings.TIME_WARP_STOP?.GetKeyDown() ?? false);

        private static void Post(string text) =>
            ScreenMessages.PostScreenMessage(text.Replace("<", "‹"), NoticeSeconds, ScreenMessageStyle.UPPER_CENTER);

        private static string Describe(WarpPolicy settings, WarpPolicyDecision decision)
        {
            if (decision.AutoDeny)
                return $"Ahora rechazarías automáticamente el warp de los demás ({decision.Reason}).";
            if (decision.AcceptUpTo > 1.0)
                return $"Ahora aceptarías automáticamente el warp de los demás hasta x{decision.AcceptUpTo:0}.";
            if (settings.AutoAccept)
                return $"Ahora no lo aceptarías automáticamente ({decision.Reason}): tendrías que subir el warp tú.";
            return "Para acompañar el warp de los demás tendrás que subirlo tú.";
        }

        private void UpdateClamp(double now)
        {
            if (!_decision.Clamped)
            {
                ResetClamp();
                return;
            }

            if (double.IsNaN(_clampedSince))
                _clampedSince = now;

            bool expired = now - _clampedSince >= RequestTimeoutSeconds;
            if (expired || CancelKeyPressed())
            {
                _decision.CancelDesire();
                ResetClamp();
                Post(expired ? "Nadie más ha acelerado el tiempo: petición de warp cancelada" : "Petición de warp cancelada");
                return;
            }

            // Un instante limitado es normal mientras el servidor procesa la petición: no avisar aún.
            string limitedBy = _clock.LimitedBy;
            if (now - _clampedSince < ClampNoticeDelaySeconds || (_clampNoticeShown && limitedBy == _clampNoticeLimitedBy))
                return;

            _clampNoticeShown = true;
            _clampNoticeLimitedBy = limitedBy;
            Post($"{ClampNotice(limitedBy)}. Se cancela en {RequestTimeoutSeconds:0} s si nadie acelera.");
        }

        private void UpdateAcceptNotice()
        {
            if (!_decision.AcceptingOthers)
            {
                _acceptNoticeShown = false;
                return;
            }

            if (_acceptNoticeShown)
                return;

            _acceptNoticeShown = true;
            Post($"Aceptando automáticamente el warp de otro jugador (x{_clock.Rate:0})");
        }

        /// <summary>Avisa una vez a quien está impidiendo que otro jugador acelere el tiempo.</summary>
        private void UpdateLimiterNotice(WarpPolicyDecision policy)
        {
            string limitedBy = _clock.LimitedBy;
            bool limitingOthers = !_decision.Clamped && !string.IsNullOrEmpty(limitedBy) && limitedBy == _localPlayerName();
            if (!limitingOthers)
            {
                _limiterNoticeShown = false;
                return;
            }

            if (_limiterNoticeShown)
                return;

            _limiterNoticeShown = true;
            if (policy.AutoDeny)
                Post($"Has rechazado automáticamente una petición de warp ({policy.Reason})");
            else if (policy.AcceptUpTo > 1.0)
                Post($"Otro jugador quiere más warp del que aceptas automáticamente (hasta x{policy.AcceptUpTo:0})");
            else
                Post("Otro jugador quiere acelerar el tiempo: sube el warp para acompañarle");
        }

        private string ClampNotice(string limitedBy)
        {
            if (limitedBy == TimeStateMessage.MixedModes)
                return "Warp limitado: hay jugadores con warp físico y otros con warp sobre raíles";
            if (string.IsNullOrEmpty(limitedBy) || limitedBy == _localPlayerName())
                return "Esperando a los demás jugadores para acelerar el tiempo";
            if (_clock.LimiterAutoDenies)
                return $"{limitedBy} rechaza automáticamente el warp";
            return $"Warp limitado por {limitedBy}";
        }

        private void ResetClamp()
        {
            _clampedSince = double.NaN;
            _clampNoticeShown = false;
            _clampNoticeLimitedBy = null;
        }

        private void Send(bool participating, double rate, WarpMode mode, double acceptUpTo, bool autoDeny)
        {
            if (_hasSent
                && participating == _sentParticipating
                && rate == _sentRate
                && mode == _sentMode
                && acceptUpTo == _sentAcceptUpTo
                && autoDeny == _sentAutoDeny)
                return;

            _hasSent = true;
            _sentParticipating = participating;
            _sentRate = rate;
            _sentMode = mode;
            _sentAcceptUpTo = acceptUpTo;
            _sentAutoDeny = autoDeny;
            ClientLog.Info($"Warp: participo={participating}, pido x{rate:0}, acepto hasta x{acceptUpTo:0}{(autoDeny ? ", rechazo automático" : string.Empty)}");
            _send(new WarpRequestMessage
            {
                Participating = participating,
                Rate = rate,
                Mode = mode,
                AcceptUpTo = acceptUpTo,
                AutoDeny = autoDeny,
            });
        }

        /// <summary>Evita convertir float[] → double[] en cada frame.</summary>
        private sealed class LadderCache
        {
            private float[] _source;
            private double[] _ladder = { 1.0 };

            public double[] Get(float[] source)
            {
                if (source == null || source.Length == 0)
                    return _ladder;

                if (!ReferenceEquals(source, _source) || source.Length != _ladder.Length)
                {
                    _source = source;
                    _ladder = new double[source.Length];
                    for (int i = 0; i < source.Length; i++)
                        _ladder[i] = source[i];
                }

                return _ladder;
            }
        }
    }
}
