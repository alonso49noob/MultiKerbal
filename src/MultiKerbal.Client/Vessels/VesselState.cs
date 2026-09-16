using MultiKerbal.Common.Messages;
using UnityEngine;

namespace MultiKerbal.Client.Vessels
{
    /// <summary>
    /// Estado cinemático de las naves y control de las "marionetas": naves de otros jugadores que KSP
    /// mantiene empaquetadas (sin física local) y que se mueven según el estado recibido.
    /// </summary>
    internal static class VesselState
    {
        public static VesselUpdateMessage Capture(Vessel vessel, double universalTime)
        {
            Orbit orbit = vessel.orbit;
            Quaternion rotation = vessel.srfRelRotation;
            return new VesselUpdateMessage
            {
                VesselId = vessel.id,
                UniversalTime = universalTime,
                BodyIndex = vessel.mainBody.flightGlobalsIndex,
                Situation = (int)vessel.situation,
                Latitude = vessel.latitude,
                Longitude = vessel.longitude,
                Altitude = vessel.altitude,
                Inclination = orbit.inclination,
                Eccentricity = orbit.eccentricity,
                SemiMajorAxis = orbit.semiMajorAxis,
                LongitudeOfAscendingNode = orbit.LAN,
                ArgumentOfPeriapsis = orbit.argumentOfPeriapsis,
                MeanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
                Epoch = orbit.epoch,
                RotationX = rotation.x,
                RotationY = rotation.y,
                RotationZ = rotation.z,
                RotationW = rotation.w,
            };
        }

        public static CelestialBody BodyOf(VesselUpdateMessage update)
        {
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body.flightGlobalsIndex == update.BodyIndex)
                    return body;
            }

            return null;
        }

        /// <summary>Copia el estado recibido a una marioneta. La posición se reajusta cada frame en <see cref="PositionPuppet"/>.</summary>
        public static void Apply(Vessel vessel, VesselUpdateMessage update)
        {
            CelestialBody body = BodyOf(update);
            if (body == null)
                return;

            var situation = (Vessel.Situations)update.Situation;
            vessel.situation = situation;
            // Posada/amerizada evita que KSP la haga estallar al creerla bajo el terreno.
            vessel.Landed = situation == Vessel.Situations.LANDED || situation == Vessel.Situations.PRELAUNCH;
            vessel.Splashed = situation == Vessel.Situations.SPLASHED;
            vessel.latitude = update.Latitude;
            vessel.longitude = update.Longitude;
            vessel.altitude = update.Altitude;
            vessel.srfRelRotation = RotationOf(update);

            vessel.orbitDriver.orbit.SetOrbit(
                update.Inclination,
                update.Eccentricity,
                update.SemiMajorAxis,
                update.LongitudeOfAscendingNode,
                update.ArgumentOfPeriapsis,
                update.MeanAnomalyAtEpoch,
                update.Epoch,
                body);
            // Empaquetada en vuelo, KSP la coloca sobre la órbita en el UT actual (así se compensa la latencia).
            vessel.orbitDriver.updateMode = update.IsOnSurface ? OrbitDriver.UpdateMode.IDLE : OrbitDriver.UpdateMode.UPDATE;
        }

        public static void PositionPuppet(Vessel vessel, VesselUpdateMessage update)
        {
            CelestialBody body = BodyOf(update);
            if (body == null)
                return;

            Quaternion rotation = body.bodyTransform.rotation * RotationOf(update);
            if (update.IsOnSurface)
            {
                vessel.SetRotation(rotation, false);
                vessel.SetPosition(body.GetWorldSurfacePosition(update.Latitude, update.Longitude, update.Altitude), true);
            }
            else
            {
                vessel.SetRotation(rotation, true);
            }
        }

        public static void MakePuppet(Vessel vessel)
        {
            var ranges = new VesselRanges(PhysicsGlobals.Instance.VesselRangesDefault);
            Puppetize(ranges.prelaunch);
            Puppetize(ranges.landed);
            Puppetize(ranges.splashed);
            Puppetize(ranges.flying);
            Puppetize(ranges.subOrbital);
            Puppetize(ranges.orbit);
            Puppetize(ranges.escaping);
            vessel.vesselRanges = ranges;

            if (!vessel.packed && vessel != FlightGlobals.ActiveVessel)
                vessel.GoOnRails();
        }

        public static void ReleasePuppet(Vessel vessel)
        {
            vessel.vesselRanges = new VesselRanges(PhysicsGlobals.Instance.VesselRangesDefault);

            // Con física activa el OrbitDriver sigue a la física (TRACK_Phys): no se toca.
            if (vessel.packed)
                vessel.orbitDriver.updateMode = vessel.LandedOrSplashed ? OrbitDriver.UpdateMode.IDLE : OrbitDriver.UpdateMode.UPDATE;
        }

        private static Quaternion RotationOf(VesselUpdateMessage update) =>
            new Quaternion(update.RotationX, update.RotationY, update.RotationZ, update.RotationW);

        /// <summary>
        /// Nunca desempaquetar (sin física local) y radio de empaquetado infinito: KSP destruye las naves empaquetadas
        /// dentro de la atmósfera salvo que estén dentro de ese radio respecto a la nave activa.
        /// </summary>
        private static void Puppetize(VesselRanges.Situation situation)
        {
            situation.unpack = 0f;
            situation.pack = float.MaxValue;
        }
    }
}
