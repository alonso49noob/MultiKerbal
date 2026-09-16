using MultiKerbal.Common.Time;
using UnityEngine;

namespace MultiKerbal.Client.Systems
{
    /// <summary>Lee de KSP la situación del jugador que usan los ajustes de warp.</summary>
    internal static class WarpContextReader
    {
        private const float NearbyDistance = 2500f;

        public static WarpContext Read(TimeWarp timeWarp, double[] railsLadder)
        {
            var context = new WarpContext
            {
                RailsMode = timeWarp.Mode == TimeWarp.Modes.HIGH,
                AtSpaceCenter = HighLogic.LoadedScene == GameScenes.SPACECENTER,
                StableSituation = true,
                EnginesOff = true,
                MaxRailsRate = railsLadder[railsLadder.Length - 1],
                IdleSeconds = IdleTracker.Seconds,
            };

            Vessel vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (vessel == null)
                return context;

            CelestialBody body = vessel.mainBody;
            Vessel.Situations situation = vessel.situation;
            context.InFlight = true;
            context.StableSituation = situation == Vessel.Situations.ORBITING
                                      || situation == Vessel.Situations.LANDED
                                      || situation == Vessel.Situations.SPLASHED
                                      || situation == Vessel.Situations.PRELAUNCH;
            context.EnginesOff = vessel.ctrlState == null || vessel.ctrlState.mainThrottle <= 0.001f;
            context.InAtmosphere = body.atmosphere && vessel.altitude < body.atmosphereDepth;
            context.VesselsNearby = HasVesselsNearby(vessel);
            context.MaxRailsRate = MaxRailsRate(vessel, body, railsLadder);
            return context;
        }

        /// <summary>Aproximación a los límites de KSP: sin warp sobre raíles en la atmósfera y límites por altitud.</summary>
        private static double MaxRailsRate(Vessel vessel, CelestialBody body, double[] railsLadder)
        {
            if (vessel.LandedOrSplashed)
                return railsLadder[railsLadder.Length - 1];
            if (body.atmosphere && vessel.altitude < body.atmosphereDepth)
                return 1.0;

            double max = 1.0;
            var limits = body.timeWarpAltitudeLimits;
            for (int i = 1; i < railsLadder.Length && limits != null && i < limits.Length; i++)
            {
                if (vessel.altitude <= limits[i])
                    break;
                max = railsLadder[i];
            }

            return max;
        }

        private static bool HasVesselsNearby(Vessel active)
        {
            Vector3 position = active.transform.position;
            foreach (Vessel other in FlightGlobals.VesselsLoaded)
            {
                if (other == active
                    || other.vesselType == VesselType.Debris
                    || other.vesselType == VesselType.Flag
                    || other.vesselType == VesselType.SpaceObject)
                    continue;

                if ((other.transform.position - position).sqrMagnitude < NearbyDistance * NearbyDistance)
                    return true;
            }

            return false;
        }
    }
}
