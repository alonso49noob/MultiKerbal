using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiKerbal.Client.Vessels
{
    /// <summary>Crear y eliminar naves en la escena actual de KSP.</summary>
    internal static class VesselSpawner
    {
        /// <summary>Devuelve null y el motivo si KSP no puede cargar la nave.</summary>
        /// <param name="besideItsCopy">
        /// En la escena sigue otra copia de la misma nave (la que esta sustituye): sus piezas tendrían los mismos
        /// identificadores internos, así que se le dan otros antes de cargarla.
        /// </param>
        public static Vessel Spawn(DecodedVessel decoded, out string error, bool besideItsCopy = false)
        {
            Game game = HighLogic.CurrentGame;
            ProtoVessel proto = CreateProto(decoded, game, out error);
            if (proto == null)
                return null;

            if (besideItsCopy)
            {
                proto.persistentId = FlightGlobals.CheckVesselpersistentId(proto.persistentId, null, false, true);
                foreach (ProtoPartSnapshot part in proto.protoPartSnapshots)
                    part.persistentId = FlightGlobals.CheckProtoPartSnapShotpersistentId(part.persistentId, null, false, true);
            }

            proto.Load(game.flightState);
            Vessel vessel = proto.vesselRef;
            if (vessel == null)
            {
                error = Loc.T("KSP no pudo cargarla", "KSP could not load it");
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

        /// <summary>La nave guardada (sin crear en la escena), con su tripulación en el plantel. Null y el motivo si faltan piezas.</summary>
        public static ProtoVessel CreateProto(DecodedVessel decoded, Game game, out string error)
        {
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
                error = Loc.T("faltan piezas: ", "missing parts: ") + string.Join(", ", missingParts.ToArray());
                return null;
            }

            error = null;
            return proto;
        }

        /// <param name="crewMoved">
        /// Su tripulación va en otra copia de la misma nave (la que la sustituye): solo se baja de esta, sin tocar su
        /// estado ni su asiento en la otra.
        /// </param>
        public static void Remove(Vessel vessel, bool crewMoved = false)
        {
            if (vessel == null || vessel == FlightGlobals.ActiveVessel)
                return;

            if (crewMoved)
                DetachCrew(vessel);
            else
                FreeCrew(vessel);
            if (vessel.protoVessel != null)
                HighLogic.CurrentGame.flightState.protoVessels.Remove(vessel.protoVessel);
            vessel.Die();
        }

        /// <summary>Nombres de los kerbals a bordo (el servidor no deja que un kerbal vaya en naves de dos jugadores).</summary>
        public static string[] CrewOf(Vessel vessel) => vessel.GetVesselCrew().Select(c => c.name).ToArray();

        /// <summary>
        /// Quitar una nave de esta partida (una copia desfasada, una que borró otro jugador...) no mata a su tripulación,
        /// que KSP daría por muerta: se bajan de la nave y quedan libres, salvo los que vayan en otra nave de la escena.
        /// </summary>
        private static void FreeCrew(Vessel vessel)
        {
            try
            {
                var freed = new List<ProtoCrewMember>();
                if (vessel.loaded)
                {
                    foreach (Part part in vessel.parts)
                    {
                        foreach (ProtoCrewMember crew in part.protoModuleCrew.ToList())
                        {
                            part.RemoveCrewmember(crew);
                            freed.Add(crew);
                        }
                    }
                }
                else if (vessel.protoVessel != null)
                {
                    foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
                    {
                        foreach (ProtoCrewMember crew in part.protoModuleCrew.ToList())
                        {
                            part.RemoveCrew(crew);
                            freed.Add(crew);
                        }
                    }
                }

                foreach (ProtoCrewMember crew in freed)
                {
                    // Muerto o perdido lo decide el plantel compartido, no quitar una copia de la nave.
                    if (crew.rosterStatus == ProtoCrewMember.RosterStatus.Dead || crew.rosterStatus == ProtoCrewMember.RosterStatus.Missing)
                        continue;

                    bool elsewhere = FlightGlobals.Vessels.Any(v => v != null && v != vessel && v.GetVesselCrew().Contains(crew));
                    crew.rosterStatus = elsewhere ? ProtoCrewMember.RosterStatus.Assigned : ProtoCrewMember.RosterStatus.Available;
                }
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo liberar la tripulación de {vessel.vesselName}: {ex.Message}");
            }
        }

        /// <summary>Vacía las listas de tripulación de la nave sin avisar a KSP: así, al destruirla, nadie muere.</summary>
        private static void DetachCrew(Vessel vessel)
        {
            try
            {
                if (vessel.loaded)
                {
                    foreach (Part part in vessel.parts)
                        part.protoModuleCrew.Clear();
                }

                if (vessel.protoVessel != null)
                {
                    foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
                        part.protoModuleCrew.Clear();
                }
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo bajar la tripulación de {vessel.vesselName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sin el kerbal en el plantel local, la carga de la pieza que lo lleva falla. Los que ya están se marcan como
        /// ocupados (aunque aquí se dieran por muertos): así no se ofrecen en el hangar, porque van en esa nave.
        /// </summary>
        private static void EnsureCrew(Game game, DecodedVessel decoded)
        {
            KerbalRoster roster = game.CrewRoster;
            foreach (ConfigNode node in decoded.CrewNodes)
            {
                string name = node.GetValue("name");
                if (!string.IsNullOrEmpty(name) && !roster.Exists(name))
                    roster.AddCrewMember(new ProtoCrewMember(game.Mode, node));
            }

            foreach (ConfigNode part in decoded.VesselNode.GetNodes("PART"))
            {
                foreach (string name in part.GetValues("crew"))
                {
                    if (string.IsNullOrEmpty(name))
                        continue;

                    // Por si faltara algún nodo KERBAL.
                    if (!roster.Exists(name))
                        roster.AddCrewMember(new ProtoCrewMember(ProtoCrewMember.KerbalType.Crew, name));

                    ProtoCrewMember crew = roster[name];
                    if (crew != null)
                        crew.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                }
            }
        }
    }
}
