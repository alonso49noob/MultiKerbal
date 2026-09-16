using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>Equivale a TimeWarp.Modes de KSP: HIGH = sobre raíles, LOW = warp físico.</summary>
    public enum WarpMode : byte
    {
        Rails = 0,
        Physics = 1,
    }

    /// <summary>
    /// Servidor → clientes. Estado del reloj compartido: UT(t) = UniversalTime + (t - ServerTime) * Rate,
    /// con t en el reloj monotónico del servidor.
    /// </summary>
    public sealed class TimeStateMessage : IMessage
    {
        /// <summary>Valor de LimitedBy cuando el warp está limitado porque los jugadores usan modos distintos.</summary>
        public const string MixedModes = "*";

        public MessageType Type => MessageType.TimeState;

        public double UniversalTime;
        public double ServerTime;
        public double Rate;
        public WarpMode Mode;

        /// <summary>Jugador que limita el warp (vacío si nadie lo limita).</summary>
        public string LimitedBy;

        /// <summary>True si quien limita el warp lo hace porque lo rechaza automáticamente.</summary>
        public bool LimiterAutoDenies;

        public void Write(PacketWriter writer)
        {
            writer.WriteDouble(UniversalTime);
            writer.WriteDouble(ServerTime);
            writer.WriteDouble(Rate);
            writer.WriteByte((byte)Mode);
            writer.WriteString(LimitedBy);
            writer.WriteBool(LimiterAutoDenies);
        }

        public void Read(PacketReader reader)
        {
            UniversalTime = reader.ReadDouble();
            ServerTime = reader.ReadDouble();
            Rate = reader.ReadDouble();
            Mode = (WarpMode)reader.ReadByte();
            LimitedBy = reader.ReadString();
            LimiterAutoDenies = reader.ReadBool();
        }
    }

    /// <summary>Cliente → servidor. Warp que el jugador quiere y cómo responde al que piden los demás.</summary>
    public sealed class WarpRequestMessage : IMessage
    {
        public MessageType Type => MessageType.WarpRequest;

        /// <summary>False si el jugador está en una escena sin tiempo (menú, hangar) y no debe limitar a nadie.</summary>
        public bool Participating;
        public double Rate;
        public WarpMode Mode;

        /// <summary>Acepta automáticamente el warp sobre raíles de los demás hasta este valor (1 = no acepta).</summary>
        public double AcceptUpTo = 1.0;

        /// <summary>Rechaza automáticamente cualquier petición de warp de los demás.</summary>
        public bool AutoDeny;

        public void Write(PacketWriter writer)
        {
            writer.WriteBool(Participating);
            writer.WriteDouble(Rate);
            writer.WriteByte((byte)Mode);
            writer.WriteDouble(AcceptUpTo);
            writer.WriteBool(AutoDeny);
        }

        public void Read(PacketReader reader)
        {
            Participating = reader.ReadBool();
            Rate = reader.ReadDouble();
            Mode = (WarpMode)reader.ReadByte();
            AcceptUpTo = reader.ReadDouble();
            AutoDeny = reader.ReadBool();
        }
    }
}
