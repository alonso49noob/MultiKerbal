using System;
using System.Collections.Generic;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Client.Systems
{
    /// <summary>
    /// Plantel compartido (en todos los modos): contrataciones, muertes, kerbals perdidos que vuelven, despidos y
    /// experiencia. Lo que pasa aquí se manda; lo que pasa en las partidas de los demás se aplica aquí.
    /// <list type="bullet">
    /// <item>Que un kerbal vaya o no a bordo lo dicen las naves (ver VesselSpawner), no esto: aquí solo se mira si
    /// sigue con vida y en el plantel.</item>
    /// <item>No se mata a un kerbal que va en la nave que pilotamos (sería un aviso viejo).</item>
    /// <item>Mientras se aplica lo de los demás no se mandan los avisos que KSP lanza por ello.</item>
    /// </list>
    /// </summary>
    internal sealed class RosterSync
    {
        private readonly Action<IMessage> _send;

        /// <summary>
        /// El kerbal va en una nave que pilota otro jugador: lo que le pase aquí no cuenta (KSP da por muerta a la
        /// tripulación de las naves ajenas que destruye por sus reglas locales). Lo cuenta la partida de su piloto.
        /// </summary>
        private readonly Func<string, bool> _aboardRemoteVessel;

        private readonly Dictionary<string, KerbalInfo> _pending = new Dictionary<string, KerbalInfo>(StringComparer.OrdinalIgnoreCase);
        private bool _applying;
        private bool _registered;

        public RosterSync(Action<IMessage> send, Func<string, bool> aboardRemoteVessel)
        {
            _send = send;
            _aboardRemoteVessel = aboardRemoteVessel;
        }

        public void Start() => Register();

        public void Reset()
        {
            Unregister();
            _pending.Clear();
        }

        public void Handle(KerbalsMessage message)
        {
            foreach (KerbalInfo kerbal in message.Kerbals)
                _pending[kerbal.Name] = kerbal;
        }

        /// <summary>Cada frame: aplica lo recibido en cuanto hay partida con plantel.</summary>
        public void Update()
        {
            if (_pending.Count == 0 || !ClientScenes.TimeFlows)
                return;

            KerbalRoster roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
                return;

            _applying = true;
            try
            {
                foreach (KerbalInfo kerbal in _pending.Values)
                    Apply(roster, kerbal);
            }
            finally
            {
                _applying = false;
                _pending.Clear();
            }
        }

        private static void Apply(KerbalRoster roster, KerbalInfo info)
        {
            try
            {
                ProtoCrewMember crew = roster.Exists(info.Name) ? roster[info.Name] : null;
                if (info.Life == KerbalLife.Sacked)
                {
                    if (crew != null && crew.rosterStatus == ProtoCrewMember.RosterStatus.Available)
                        roster.Remove(crew);
                    return;
                }

                if (crew == null)
                {
                    if (info.Life != KerbalLife.Alive)
                        return; // No hace falta crear a un kerbal muerto que aquí no existía.

                    ConfigNode node = ParseNode(info.Node);
                    if (node == null)
                        return;

                    crew = new ProtoCrewMember(HighLogic.CurrentGame.Mode, node);
                    crew.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    crew.type = ProtoCrewMember.KerbalType.Crew;
                    roster.AddCrewMember(crew);
                    ClientLog.Info($"Kerbal contratado por otro jugador: {info.Name}");
                    return;
                }

                switch (info.Life)
                {
                    case KerbalLife.Dead:
                    case KerbalLife.Missing:
                        if (IsAboardActiveVessel(crew))
                            return;

                        ProtoCrewMember.RosterStatus status = info.Life == KerbalLife.Dead
                            ? ProtoCrewMember.RosterStatus.Dead
                            : ProtoCrewMember.RosterStatus.Missing;
                        if (crew.rosterStatus != status)
                        {
                            crew.rosterStatus = status;
                            ClientLog.Info($"{info.Name}: {status} en la partida de otro jugador");
                        }

                        break;
                    case KerbalLife.Alive:
                        if (crew.rosterStatus == ProtoCrewMember.RosterStatus.Dead || crew.rosterStatus == ProtoCrewMember.RosterStatus.Missing)
                            crew.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        if (crew.type == ProtoCrewMember.KerbalType.Applicant)
                            crew.type = ProtoCrewMember.KerbalType.Crew;
                        CopyExperience(crew, ParseNode(info.Node));
                        break;
                }
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo aplicar el kerbal {info.Name}: {ex.Message}");
            }
        }

        private static bool IsAboardActiveVessel(ProtoCrewMember crew)
        {
            Vessel active = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            return active != null && active.GetVesselCrew().Contains(crew);
        }

        /// <summary>La experiencia solo sube: se queda la mayor.</summary>
        private static void CopyExperience(ProtoCrewMember crew, ConfigNode node)
        {
            if (node == null)
                return;

            var remote = new ProtoCrewMember(HighLogic.CurrentGame.Mode, node);
            if (remote.experience <= crew.experience)
                return;

            crew.experience = remote.experience;
            crew.experienceLevel = Math.Max(crew.experienceLevel, remote.experienceLevel);
        }

        private static ConfigNode ParseNode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            ConfigNode parsed = ConfigNode.Parse(text);
            return parsed?.GetNode("KERBAL") ?? parsed;
        }

        private void Register()
        {
            if (_registered)
                return;

            _registered = true;
            GameEvents.OnCrewmemberHired.Add(OnHired);
            GameEvents.OnCrewmemberSacked.Add(OnSacked);
            GameEvents.onKerbalStatusChange.Add(OnStatusChange);
            GameEvents.onKerbalLevelUp.Add(OnLevelUp);
        }

        private void Unregister()
        {
            if (!_registered)
                return;

            _registered = false;
            GameEvents.OnCrewmemberHired.Remove(OnHired);
            GameEvents.OnCrewmemberSacked.Remove(OnSacked);
            GameEvents.onKerbalStatusChange.Remove(OnStatusChange);
            GameEvents.onKerbalLevelUp.Remove(OnLevelUp);
        }

        private void OnHired(ProtoCrewMember crew, int count) => Send(crew, KerbalLife.Alive);

        private void OnSacked(ProtoCrewMember crew, int count) => Send(crew, KerbalLife.Sacked);

        private void OnLevelUp(ProtoCrewMember crew) => Send(crew, KerbalLife.Alive);

        /// <summary>Solo interesan las muertes, los perdidos y los que vuelven (subir y bajar de naves no).</summary>
        private void OnStatusChange(ProtoCrewMember crew, ProtoCrewMember.RosterStatus from, ProtoCrewMember.RosterStatus to)
        {
            if (crew == null || _aboardRemoteVessel(crew.name))
                return;

            if (to == ProtoCrewMember.RosterStatus.Dead)
                Send(crew, KerbalLife.Dead);
            else if (to == ProtoCrewMember.RosterStatus.Missing)
                Send(crew, KerbalLife.Missing);
            else if (from == ProtoCrewMember.RosterStatus.Dead || from == ProtoCrewMember.RosterStatus.Missing)
                Send(crew, KerbalLife.Alive);
        }

        private void Send(ProtoCrewMember crew, KerbalLife life)
        {
            if (_applying || crew == null || string.IsNullOrEmpty(crew.name) || crew.type == ProtoCrewMember.KerbalType.Applicant)
                return;

            try
            {
                var node = new ConfigNode("KERBAL");
                crew.Save(node);
                _send(new KerbalsMessage { Kerbals = new[] { new KerbalInfo { Name = crew.name, Life = life, Node = node.ToString() } } });
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo compartir el kerbal {crew.name}: {ex.Message}");
            }
        }
    }
}
