using System;
using System.Collections.Generic;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Common.Time
{
    public struct WarpVote
    {
        public string PlayerName;
        public bool Participating;
        public double Rate;
        public WarpMode Mode;

        /// <summary>Acepta automáticamente el warp sobre raíles de los demás hasta este valor (≤ 1 = no acepta).</summary>
        public double AcceptUpTo;

        public bool AutoDeny;
    }

    public struct WarpConsensusResult
    {
        public double Rate;
        public WarpMode Mode;
        public string LimitedBy;
        public bool LimiterAutoDenies;
    }

    /// <summary>
    /// Reloj compartido: el universo avanza al warp más lento de los jugadores que participan.
    /// Quien acepta automáticamente el warp de los demás cuenta como si pidiera lo mismo que el que más pide
    /// (hasta su límite). Warp sobre raíles y físico no se mezclan (x1), y el físico nunca se acepta automáticamente.
    /// </summary>
    public static class WarpConsensus
    {
        private const double MaxRate = 1e7;

        public static WarpConsensusResult Compute(IEnumerable<WarpVote> votes)
        {
            var participants = new List<WarpVote>();
            double maxDesire = 1.0;
            bool anyRails = false;
            bool anyPhysics = false;

            foreach (WarpVote vote in votes)
            {
                if (!vote.Participating)
                    continue;

                participants.Add(vote);
                double rate = Normalize(vote.Rate);
                if (rate > 1.0)
                {
                    if (vote.Mode == WarpMode.Physics)
                        anyPhysics = true;
                    else
                        anyRails = true;
                }

                if (rate > maxDesire)
                    maxDesire = rate;
            }

            if (participants.Count == 0)
                return new WarpConsensusResult { Rate = 1.0, Mode = WarpMode.Rails, LimitedBy = string.Empty };

            if (anyRails && anyPhysics)
                return new WarpConsensusResult { Rate = 1.0, Mode = WarpMode.Rails, LimitedBy = TimeStateMessage.MixedModes };

            WarpMode mode = anyPhysics ? WarpMode.Physics : WarpMode.Rails;
            double result = double.MaxValue;
            WarpVote limiter = default;
            foreach (WarpVote vote in participants)
            {
                double limit = Normalize(vote.Rate);
                if (mode == WarpMode.Rails && !vote.AutoDeny)
                    limit = Math.Max(limit, Math.Min(Normalize(vote.AcceptUpTo), maxDesire));

                if (limit < result)
                {
                    result = limit;
                    limiter = vote;
                }
            }

            bool limited = maxDesire > result;
            return new WarpConsensusResult
            {
                Rate = result,
                Mode = mode,
                LimitedBy = limited ? limiter.PlayerName ?? string.Empty : string.Empty,
                LimiterAutoDenies = limited && limiter.AutoDeny,
            };
        }

        private static double Normalize(double rate) =>
            double.IsNaN(rate) ? 1.0 : Math.Min(MaxRate, Math.Max(1.0, rate));
    }
}
