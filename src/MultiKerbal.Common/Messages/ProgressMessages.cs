using System;
using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>Nivel de un edificio del Centro Espacial (0 = sin mejorar, 1 = al máximo), como lo guarda KSP.</summary>
    public struct FacilityLevel
    {
        /// <summary>Identificador de KSP, p. ej. "SpaceCenter/LaunchPad".</summary>
        public string Id;

        public float Level;
    }

    /// <summary>
    /// Servidor → cliente. Progreso compartido completo: al entrar y después de cada cambio de cualquier jugador.
    /// En sandbox no se envía. Los experimentos, contratos y kerbals van aparte (son muchos y cambian de uno en uno).
    /// </summary>
    public sealed class ProgressStateMessage : IMessage
    {
        public MessageType Type => MessageType.ProgressState;

        public double Funds;
        public double Science;
        public double Reputation;

        /// <summary>Identificadores de los nodos del árbol tecnológico investigados (techID de KSP).</summary>
        public string[] Techs = new string[0];

        /// <summary>Piezas compradas en carrera (nombre interno de KSP).</summary>
        public string[] Parts = new string[0];

        public FacilityLevel[] Facilities = new FacilityLevel[0];

        public void Write(PacketWriter writer)
        {
            writer.WriteDouble(Funds);
            writer.WriteDouble(Science);
            writer.WriteDouble(Reputation);
            writer.WriteStrings(Techs);
            writer.WriteStrings(Parts);
            Facilities = Facilities ?? new FacilityLevel[0];
            writer.WriteInt32(Facilities.Length);
            foreach (FacilityLevel facility in Facilities)
            {
                writer.WriteString(facility.Id);
                writer.WriteSingle(facility.Level);
            }
        }

        public void Read(PacketReader reader)
        {
            Funds = reader.ReadDouble();
            Science = reader.ReadDouble();
            Reputation = reader.ReadDouble();
            Techs = reader.ReadStrings(ProtocolInfo.MaxTechNodes);
            Parts = reader.ReadStrings(ProtocolInfo.MaxPurchasedParts);
            int count = reader.ReadCount(ProtocolInfo.MaxFacilities);
            Facilities = new FacilityLevel[count];
            for (int i = 0; i < count; i++)
                Facilities[i] = new FacilityLevel { Id = reader.ReadString() ?? string.Empty, Level = reader.ReadSingle() };
        }
    }

    /// <summary>
    /// Cliente → servidor. Un cambio en la partida de un jugador: fondos, ciencia o reputación como diferencia
    /// (así no se pisan los cambios simultáneos de dos jugadores), una tecnología investigada, una pieza comprada
    /// o un edificio mejorado.
    /// </summary>
    public sealed class ProgressChangeMessage : IMessage
    {
        public MessageType Type => MessageType.ProgressChange;

        public ProgressKind Kind;

        /// <summary>Diferencia de fondos, ciencia o reputación; nivel (0..1) de un edificio.</summary>
        public double Amount;

        /// <summary>techID, nombre de la pieza o identificador del edificio.</summary>
        public string Id;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)Kind);
            writer.WriteDouble(Amount);
            writer.WriteString(Id);
        }

        public void Read(PacketReader reader)
        {
            Kind = (ProgressKind)reader.ReadByte();
            Amount = reader.ReadDouble();
            Id = reader.ReadString();
        }
    }

    /// <summary>Lo ya investigado de un experimento en un sitio: la ciencia que da baja al repetirlo.</summary>
    public struct ScienceSubjectInfo
    {
        /// <summary>Identificador de KSP, p. ej. "crewReport@KerbinSrfLandedLaunchPad".</summary>
        public string Id;

        public string Title;
        public float DataScale;
        public float SubjectValue;
        public float ScienceCap;

        /// <summary>Ciencia ya conseguida (solo sube).</summary>
        public float Science;

        /// <summary>Lo que queda por sacar (1 = nada hecho, 0 = agotado).</summary>
        public float ScientificValue;
    }

    /// <summary>
    /// Ambos sentidos. Experimentos con ciencia conseguida. El cliente manda uno al recibir ciencia; el servidor guarda
    /// el de más ciencia y lo reenvía a los demás. Al entrar, el servidor manda todos.
    /// </summary>
    public sealed class ScienceSubjectsMessage : IMessage
    {
        public MessageType Type => MessageType.ScienceSubjects;

        public ScienceSubjectInfo[] Subjects = new ScienceSubjectInfo[0];

        public void Write(PacketWriter writer)
        {
            Subjects = Subjects ?? new ScienceSubjectInfo[0];
            writer.WriteInt32(Subjects.Length);
            foreach (ScienceSubjectInfo subject in Subjects)
            {
                writer.WriteString(subject.Id);
                writer.WriteString(subject.Title);
                writer.WriteSingle(subject.DataScale);
                writer.WriteSingle(subject.SubjectValue);
                writer.WriteSingle(subject.ScienceCap);
                writer.WriteSingle(subject.Science);
                writer.WriteSingle(subject.ScientificValue);
            }
        }

        public void Read(PacketReader reader)
        {
            int count = reader.ReadCount(ProtocolInfo.MaxScienceSubjects);
            Subjects = new ScienceSubjectInfo[count];
            for (int i = 0; i < count; i++)
            {
                Subjects[i] = new ScienceSubjectInfo
                {
                    Id = reader.ReadString() ?? string.Empty,
                    Title = reader.ReadString() ?? string.Empty,
                    DataScale = reader.ReadSingle(),
                    SubjectValue = reader.ReadSingle(),
                    ScienceCap = reader.ReadSingle(),
                    Science = reader.ReadSingle(),
                    ScientificValue = reader.ReadSingle(),
                };
            }
        }
    }

    /// <summary>Estado de un contrato (los valores de Contract.State de KSP).</summary>
    public enum ContractStatus : byte
    {
        Active = 5,
        Completed = 6,
        DeadlineExpired = 7,
        Failed = 8,
        Cancelled = 4,
    }

    /// <summary>Un contrato aceptado o terminado, con su definición de KSP (nodo CONTRACT en texto).</summary>
    public struct ContractInfo
    {
        public Guid Id;
        public ContractStatus Status;
        public string Node;
    }

    /// <summary>
    /// Ambos sentidos (carrera). Las ofertas son de cada jugador, pero un contrato aceptado es de todos: el cliente
    /// manda el que acepta o termina; el servidor lo guarda y lo reenvía. Al entrar, el servidor manda todos.
    /// </summary>
    public sealed class ContractsMessage : IMessage
    {
        public MessageType Type => MessageType.Contracts;

        public ContractInfo[] Contracts = new ContractInfo[0];

        public void Write(PacketWriter writer)
        {
            Contracts = Contracts ?? new ContractInfo[0];
            writer.WriteInt32(Contracts.Length);
            foreach (ContractInfo contract in Contracts)
            {
                writer.WriteGuid(contract.Id);
                writer.WriteByte((byte)contract.Status);
                writer.WriteString(contract.Node);
            }
        }

        public void Read(PacketReader reader)
        {
            int count = reader.ReadCount(ProtocolInfo.MaxContracts);
            Contracts = new ContractInfo[count];
            for (int i = 0; i < count; i++)
            {
                Contracts[i] = new ContractInfo
                {
                    Id = reader.ReadGuid(),
                    Status = (ContractStatus)reader.ReadByte(),
                    Node = reader.ReadString() ?? string.Empty,
                };
            }
        }
    }

    /// <summary>Si un kerbal sigue en el plantel (en cualquier modo de juego).</summary>
    public enum KerbalLife : byte
    {
        Alive = 0,
        Dead = 1,

        /// <summary>Perdido (en carrera vuelve al cabo de un tiempo si la dificultad lo permite).</summary>
        Missing = 2,

        /// <summary>Despedido: ya no está en el plantel.</summary>
        Sacked = 3,
    }

    /// <summary>Un kerbal del plantel compartido, con su definición de KSP (nodo KERBAL en texto).</summary>
    public struct KerbalInfo
    {
        public string Name;
        public KerbalLife Life;
        public string Node;
    }

    /// <summary>
    /// Ambos sentidos (todos los modos). Contrataciones, muertes, regresos y experiencia: el cliente manda el kerbal
    /// que cambia; el servidor guarda el último estado y lo reenvía. Al entrar, el servidor manda todos.
    /// </summary>
    public sealed class KerbalsMessage : IMessage
    {
        public MessageType Type => MessageType.Kerbals;

        public KerbalInfo[] Kerbals = new KerbalInfo[0];

        public void Write(PacketWriter writer)
        {
            Kerbals = Kerbals ?? new KerbalInfo[0];
            writer.WriteInt32(Kerbals.Length);
            foreach (KerbalInfo kerbal in Kerbals)
            {
                writer.WriteString(kerbal.Name);
                writer.WriteByte((byte)kerbal.Life);
                writer.WriteString(kerbal.Node);
            }
        }

        public void Read(PacketReader reader)
        {
            int count = reader.ReadCount(ProtocolInfo.MaxKerbals);
            Kerbals = new KerbalInfo[count];
            for (int i = 0; i < count; i++)
            {
                Kerbals[i] = new KerbalInfo
                {
                    Name = reader.ReadString() ?? string.Empty,
                    Life = (KerbalLife)reader.ReadByte(),
                    Node = reader.ReadString() ?? string.Empty,
                };
            }
        }
    }
}
