using System;
using MultiKerbal.Common.Time;
using UnityEngine;

namespace MultiKerbal.Client.Systems
{
    /// <summary>
    /// Mantiene el UT local pegado al reloj del servidor: errores pequeños se corrigen poco a poco
    /// y los grandes (pausa, salida del hangar, tirones de red) con un salto.
    /// </summary>
    internal sealed class TimeSyncSystem
    {
        private const double HardSyncMinSeconds = 2.0;
        /// <summary>En warp, el umbral de salto equivale a este tiempo real.</summary>
        private const double HardSyncRealSeconds = 0.5;
        private const double DeadbandRealSeconds = 0.02;
        private const double NudgeGainPerSecond = 1.0;

        private readonly SharedClock _clock;
        private bool _forceHardSync = true;

        public TimeSyncSystem(SharedClock clock)
        {
            _clock = clock;
        }

        /// <summary>Servidor − local en el último frame, en segundos de juego.</summary>
        public double LastError { get; private set; }

        public void RequestHardSync() => _forceHardSync = true;

        public void Update()
        {
            if (!ClientScenes.TimeFlows || !_clock.HasState || Planetarium.fetch == null)
            {
                // Al volver a una escena con tiempo (p. ej. desde el hangar) hay que saltar al presente.
                _forceHardSync = true;
                return;
            }

            // En pausa o cargando el vuelo: se corrige al reanudar.
            if (HighLogic.LoadedSceneIsFlight && (FlightDriver.Pause || !FlightGlobals.ready))
                return;

            double target = _clock.EstimateUniversalTime(LocalClock.Now);
            double local = Planetarium.GetUniversalTime();
            double error = target - local;
            double rate = Math.Max(1.0, _clock.Rate);
            LastError = error;

            if (_forceHardSync || Math.Abs(error) > Math.Max(HardSyncMinSeconds, rate * HardSyncRealSeconds))
            {
                HardSync(target, error);
                _forceHardSync = false;
                return;
            }

            if (Math.Abs(error) < DeadbandRealSeconds * rate)
                return;

            double step = error * Math.Min(1.0, NudgeGainPerSecond * Time.unscaledDeltaTime);
            Planetarium.SetUniversalTime(local + step);
        }

        private static void HardSync(double target, double error)
        {
            // Las naves con física no se empaquetan: KSP recalcula su órbita desde la física en cada frame, así que el
            // salto no las mueve. Empaquetar la nave activa en pleno vuelo interrumpía la física y el control.
            Planetarium.SetUniversalTime(target);
            if (Math.Abs(error) >= 1.0)
                ClientLog.Info($"Tiempo sincronizado ({error:+0.0;-0.0} s): {KerbalTime.Format(target)}");
        }
    }
}
