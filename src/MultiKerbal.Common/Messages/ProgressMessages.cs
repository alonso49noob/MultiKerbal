using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>
    /// Servidor → cliente. Progreso compartido completo: al entrar y después de cada cambio de cualquier jugador.
    /// En sandbox no se envía.
    /// </summary>
    public sealed class ProgressStateMessage : IMessage
    {
        public MessageType Type => MessageType.ProgressState;

        public double Funds;
        public double Science;
        public double Reputation;

        /// <summary>Identificadores de los nodos del árbol tecnológico investigados (techID de KSP).</summary>
        public string[] Techs = new string[0];

        public void Write(PacketWriter writer)
        {
            writer.WriteDouble(Funds);
            writer.WriteDouble(Science);
            writer.WriteDouble(Reputation);
            writer.WriteStrings(Techs);
        }

        public void Read(PacketReader reader)
        {
            Funds = reader.ReadDouble();
            Science = reader.ReadDouble();
            Reputation = reader.ReadDouble();
            Techs = reader.ReadStrings(ProtocolInfo.MaxTechNodes);
        }
    }

    /// <summary>
    /// Cliente → servidor. Un cambio en la partida de un jugador: fondos, ciencia o reputación como diferencia
    /// (así no se pisan los cambios simultáneos de dos jugadores) o una tecnología investigada.
    /// </summary>
    public sealed class ProgressChangeMessage : IMessage
    {
        public MessageType Type => MessageType.ProgressChange;

        public ProgressKind Kind;

        /// <summary>Diferencia de fondos, ciencia o reputación.</summary>
        public double Amount;

        /// <summary>Solo con <see cref="ProgressKind.Tech"/>.</summary>
        public string TechId;

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)Kind);
            writer.WriteDouble(Amount);
            writer.WriteString(TechId);
        }

        public void Read(PacketReader reader)
        {
            Kind = (ProgressKind)reader.ReadByte();
            Amount = reader.ReadDouble();
            TechId = reader.ReadString();
        }
    }
}
