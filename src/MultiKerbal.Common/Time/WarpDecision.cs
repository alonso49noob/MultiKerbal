using System;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Common.Time
{
    /// <summary>
    /// Decide cada frame qué índice de TimeWarp aplicar en el cliente. Separa lo que el jugador quiere
    /// (se envía al servidor) de lo que el consenso permite (se aplica en local), y aplica además el warp
    /// de otros jugadores que se acepte automáticamente. Lógica pura, sin KSP, para poder probarla.
    /// </summary>
    public sealed class WarpDecision
    {
        private bool _initialized;
        private int _lastAppliedIndex;
        private WarpMode _lastAppliedMode;
        private double _acceptUpToRate = 1.0;
        private int _acceptRefusedIndex = int.MaxValue;

        public WarpMode DesiredMode { get; private set; }

        public int DesiredIndex { get; private set; }

        /// <summary>True si el jugador quiere más warp del que permite el consenso.</summary>
        public bool Clamped { get; private set; }

        /// <summary>True si se aplica más warp del que pidió el jugador porque acepta el de otros.</summary>
        public bool AcceptingOthers { get; private set; }

        public void Reset()
        {
            _initialized = false;
            Clamped = false;
            AcceptingOthers = false;
            _acceptUpToRate = 1.0;
            _acceptRefusedIndex = int.MaxValue;
        }

        public int Update(WarpMode currentMode, int currentIndex, double[] ladder, double grantedRate, WarpMode grantedMode, out bool requestChanged) =>
            Update(currentMode, currentIndex, ladder, grantedRate, grantedMode, 1.0, out requestChanged);

        /// <param name="currentMode">Modo actual de TimeWarp.</param>
        /// <param name="currentIndex">Índice actual de TimeWarp.</param>
        /// <param name="ladder">Multiplicadores del modo actual (índice 0 = x1).</param>
        /// <param name="grantedRate">Warp permitido por el servidor.</param>
        /// <param name="grantedMode">Modo del warp permitido.</param>
        /// <param name="acceptUpToRate">Warp sobre raíles de los demás que se aplica sin pedirlo (1 = ninguno).</param>
        /// <param name="requestChanged">True si ha cambiado lo que quiere el jugador.</param>
        /// <returns>Índice a aplicar, o -1 si no hay que tocar TimeWarp.</returns>
        public int Update(WarpMode currentMode, int currentIndex, double[] ladder, double grantedRate, WarpMode grantedMode, double acceptUpToRate, out bool requestChanged)
        {
            int maxIndex = ladder.Length - 1;
            currentIndex = Clamp(currentIndex, 0, maxIndex);
            requestChanged = false;

            if (acceptUpToRate != _acceptUpToRate)
            {
                _acceptUpToRate = acceptUpToRate;
                _acceptRefusedIndex = int.MaxValue;
            }

            if (!_initialized)
            {
                _initialized = true;
                DesiredMode = currentMode;
                DesiredIndex = currentIndex;
                requestChanged = true;
            }
            else if (currentMode != _lastAppliedMode)
            {
                DesiredMode = currentMode;
                DesiredIndex = currentIndex;
                requestChanged = true;
            }
            else if (currentIndex > _lastAppliedIndex)
            {
                // Se sube sobre lo mayor entre lo deseado y lo aplicado: si está limitado, el jugador puede seguir
                // pidiendo más; si está aceptando warp ajeno, pide a partir de lo que ya ve.
                int desired = Clamp(Math.Max(DesiredIndex, _lastAppliedIndex) + (currentIndex - _lastAppliedIndex), 0, maxIndex);
                if (desired != DesiredIndex)
                {
                    DesiredIndex = desired;
                    requestChanged = true;
                }
            }
            else if (currentIndex < _lastAppliedIndex)
            {
                // Si lo aplicado venía de aceptar el warp de otros, la bajada (de KSP o del jugador) limita lo que se acepta.
                if (_lastAppliedIndex > DesiredIndex)
                    _acceptRefusedIndex = currentIndex;

                // Bajada manual o impuesta por KSP (atmósfera, cambio de SOI...).
                if (currentIndex < DesiredIndex)
                {
                    DesiredIndex = currentIndex;
                    requestChanged = true;
                }
            }

            DesiredIndex = Clamp(DesiredIndex, 0, maxIndex);

            int acceptIndex = currentMode == WarpMode.Rails
                ? IndexAtOrBelow(ladder, Math.Min(_acceptRefusedIndex, maxIndex), acceptUpToRate)
                : 0;
            double allowed = grantedMode == DesiredMode ? grantedRate : 1.0;
            int target = IndexAtOrBelow(ladder, Math.Max(DesiredIndex, acceptIndex), allowed);

            Clamped = target < DesiredIndex;
            AcceptingOthers = target > DesiredIndex;
            _lastAppliedMode = currentMode;
            _lastAppliedIndex = target;
            return target != currentIndex ? target : -1;
        }

        public double DesiredRate(double[] ladder) => ladder[Clamp(DesiredIndex, 0, ladder.Length - 1)];

        /// <summary>
        /// Modo que se envía al servidor. A x1 el modo no afecta al consenso, así que se envía siempre "raíles":
        /// KSP cambia de modo por su cuenta y no debe generar peticiones nuevas.
        /// </summary>
        public WarpMode RequestedMode(double[] ladder) => DesiredRate(ladder) > 1.0 ? DesiredMode : WarpMode.Rails;

        /// <summary>Lo que realmente se acepta: el ajuste del jugador, recortado si ya bajó (o KSP bajó) un warp aceptado.</summary>
        public double EffectiveAcceptUpTo(double[] railsLadder, double acceptUpToRate) =>
            Math.Min(acceptUpToRate, railsLadder[Clamp(_acceptRefusedIndex, 0, railsLadder.Length - 1)]);

        /// <summary>Cuando ya nadie pide warp, se vuelve a aceptar hasta el límite del ajuste.</summary>
        public void ResetAcceptRefusal() => _acceptRefusedIndex = int.MaxValue;

        /// <summary>Renuncia a lo pedido: lo deseado pasa a ser lo que se está aplicando.</summary>
        public void CancelDesire()
        {
            if (!_initialized)
                return;

            DesiredMode = _lastAppliedMode;
            DesiredIndex = _lastAppliedIndex;
            Clamped = false;
        }

        private static int IndexAtOrBelow(double[] ladder, int fromIndex, double rate)
        {
            for (int i = fromIndex; i > 0; i--)
            {
                if (ladder[i] <= rate * (1.0 + 1e-6))
                    return i;
            }

            return 0;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}
