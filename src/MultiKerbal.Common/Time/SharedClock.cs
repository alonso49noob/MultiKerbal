using System;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Common.Time
{
    /// <summary>El reloj universal compartido tal como lo estima un cliente.</summary>
    public sealed class SharedClock
    {
        private TimeStateMessage _state;

        public ClockSync Sync { get; } = new ClockSync();

        public bool HasState => _state != null;

        public double Rate => _state?.Rate ?? 1.0;

        public WarpMode Mode => _state?.Mode ?? WarpMode.Rails;

        public string LimitedBy => _state?.LimitedBy ?? string.Empty;

        public bool LimiterAutoDenies => _state?.LimiterAutoDenies ?? false;

        public void ApplyState(TimeStateMessage state, double localTime)
        {
            Sync.SeedIfUnsynchronized(localTime, state.ServerTime);
            _state = state;
        }

        public void AddPong(PongMessage pong, double localReceiveTime)
        {
            Sync.AddSample(pong.ClientTime, localReceiveTime, pong.ServerTime);
        }

        /// <summary>UT del servidor en este instante, o NaN si aún no hay estado.</summary>
        public double EstimateUniversalTime(double localTime)
        {
            if (_state == null)
                return double.NaN;

            double serverNow = Sync.ToServerTime(localTime);
            // Max: el ruido de la estimación no debe hacer retroceder el tiempo respecto al último estado.
            return _state.UniversalTime + Math.Max(0.0, serverNow - _state.ServerTime) * _state.Rate;
        }

        public void Reset()
        {
            Sync.Reset();
            _state = null;
        }
    }
}
