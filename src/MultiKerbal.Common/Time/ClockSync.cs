using System;

namespace MultiKerbal.Common.Time
{
    /// <summary>
    /// Estima el reloj monotónico del servidor a partir de pings (NTP simplificado).
    /// Usa la muestra con menor RTT de la ventana porque es la que menos error de asimetría tiene.
    /// </summary>
    public sealed class ClockSync
    {
        private const int WindowSize = 8;

        private readonly double[] _offsets = new double[WindowSize];
        private readonly double[] _roundTrips = new double[WindowSize];
        private int _count;
        private int _next;

        public bool IsSynchronized => _count > 0;

        /// <summary>Reloj del servidor − reloj local, en segundos.</summary>
        public double Offset { get; private set; }

        /// <summary>Último RTT medido, en segundos.</summary>
        public double RoundTripTime { get; private set; }

        /// <param name="localSendTime">Reloj local al enviar el ping.</param>
        /// <param name="localReceiveTime">Reloj local al recibir el pong.</param>
        /// <param name="serverTime">Reloj del servidor al recibir el ping.</param>
        public void AddSample(double localSendTime, double localReceiveTime, double serverTime)
        {
            double roundTrip = Math.Max(0.0, localReceiveTime - localSendTime);
            // Suponiendo ida y vuelta simétricas, el servidor recibió el ping a mitad del RTT.
            double offset = serverTime - (localSendTime + roundTrip / 2.0);

            _offsets[_next] = offset;
            _roundTrips[_next] = roundTrip;
            _next = (_next + 1) % WindowSize;
            if (_count < WindowSize)
                _count++;

            int best = 0;
            for (int i = 1; i < _count; i++)
            {
                if (_roundTrips[i] < _roundTrips[best])
                    best = i;
            }

            Offset = _offsets[best];
            RoundTripTime = roundTrip;
        }

        /// <summary>Estimación inicial sin latencia, útil antes del primer pong.</summary>
        public void SeedIfUnsynchronized(double localTime, double serverTime)
        {
            if (_count == 0)
                Offset = serverTime - localTime;
        }

        public double ToServerTime(double localTime) => localTime + Offset;

        public void Reset()
        {
            _count = 0;
            _next = 0;
            Offset = 0;
            RoundTripTime = 0;
        }
    }
}
