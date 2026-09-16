using System.Collections.Generic;
using System.Linq;

namespace MultiKerbal.Client.Vessels
{
    /// <summary>Crear y eliminar naves en la escena actual de KSP.</summary>
    internal static class VesselSpawner
    {
        /// <summary>Devuelve null y el motivo si KSP no puede cargar la nave.</summary>
        public static Vessel Spawn(DecodedVessel decoded, out string error)
        {
            Game game = HighLogic.CurrentGame;
            EnsureCrew(game, decoded);

            var proto = new ProtoVessel(decoded.VesselNode, game);
            List<string> missingParts = proto.protoPartSnapshots
                .Where(p => p.partInfo == null || p.partInfo.partPrefab == null)
                .Select(p => p.partName)
                .Distinct()
                .ToList();
            if (missingParts.Count > 0)
            {
                // ProtoVessel.Load abriría una ventana de error que persiste entre escenas: mejor comprobarlo antes.
                error = "faltan piezas: " + string.Join(", ", missingParts.ToArray());
                return null;
            }

            proto.Load(game.flightState);
            Vessel vessel = proto.vesselRef;
            if (vessel == null)
            {
                error = "KSP no pudo cargarla";
                return null;
            }

            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                // Lo mismo que hace KSP al generar naves de contratos fuera del vuelo.
                proto.persistentId = FlightGlobals.CheckVesselpersistentId(proto.persistentId, null, false, true);
                vessel.persistentId = proto.persistentId;
                foreach (ProtoPartSnapshot part in proto.protoPartSnapshots)
                    part.persistentId = FlightGlobals.CheckProtoPartSnapShotpersistentId(part.persistentId, null, false, true);
                game.flightState.protoVessels.Add(proto);
            }

            error = null;
            return vessel;
        }

        public static void Remove(Vessel vessel)
        {
            if (vessel == null || vessel == FlightGlobals.ActiveVessel)
                return;

            if (vessel.protoVessel != null)
                HighLogic.CurrentGame.flightState.protoVessels.Remove(vessel.protoVessel);
            vessel.Die();
        }

        /// <summary>Sin el kerbal en el plantel local, la carga de la pieza que lo lleva falla.</summary>
        private static void EnsureCrew(Game game, DecodedVessel decoded)
        {
            KerbalRoster roster = game.CrewRoster;
            foreach (ConfigNode node in decoded.CrewNodes)
            {
                string name = node.GetValue("name");
                if (!string.IsNullOrEmpty(name) && !roster.Exists(name))
                    roster.AddCrewMember(new ProtoCrewMember(game.Mode, node));
            }

            // Por si faltara algún nodo KERBAL.
            foreach (ConfigNode part in decoded.VesselNode.GetNodes("PART"))
            {
                foreach (string name in part.GetValues("crew"))
                {
                    if (!string.IsNullOrEmpty(name) && !roster.Exists(name))
                        roster.AddCrewMember(new ProtoCrewMember(ProtoCrewMember.KerbalType.Crew, name));
                }
            }
        }
    }
}
