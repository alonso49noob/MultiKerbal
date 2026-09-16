using UnityEngine;

namespace MultiKerbal.Client.Systems
{
    /// <summary>
    /// Tiempo que lleva el jugador sin tocar teclado ni ratón. Si KSP está en segundo plano no recibe entradas,
    /// así que también cuenta como ausente.
    /// </summary>
    internal static class IdleTracker
    {
        private static Vector3 _lastMousePosition;
        private static double _lastInput;
        private static bool _started;

        /// <summary>Segundos sin actividad.</summary>
        public static double Seconds => _started ? LocalClock.Now - _lastInput : 0.0;

        /// <summary>Llamar una vez por frame.</summary>
        public static void Poll()
        {
            double now = LocalClock.Now;
            Vector3 mouse = Input.mousePosition;
            if (!_started)
            {
                _started = true;
                _lastInput = now;
                _lastMousePosition = mouse;
                return;
            }

            if (Input.anyKey || mouse != _lastMousePosition || Input.mouseScrollDelta.sqrMagnitude > 0f)
            {
                _lastInput = now;
                _lastMousePosition = mouse;
            }
        }
    }
}
