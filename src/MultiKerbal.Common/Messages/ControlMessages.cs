using System;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>Acciones que un copiloto puede pedirle a quien pilota. Los números viajan por la red: no reordenar.</summary>
    public enum VesselAction : byte
    {
        Stage = 0,
        Gear = 1,
        Brakes = 2,
        Lights = 3,
        Rcs = 4,
        Sas = 5,
        Abort = 6,
        Custom01 = 11,
        Custom02 = 12,
        Custom03 = 13,
        Custom04 = 14,
        Custom05 = 15,
        Custom06 = 16,
        Custom07 = 17,
        Custom08 = 18,
        Custom09 = 19,
        Custom10 = 20,
    }

    /// <summary>Cliente → servidor. "Pídele el control de esta nave a quien la pilota".</summary>
    public sealed class VesselHandoverRequestMessage : IMessage
    {
        public MessageType Type => MessageType.VesselHandoverRequest;

        public Guid VesselId;

        public void Write(PacketWriter writer) => writer.WriteGuid(VesselId);

        public void Read(PacketReader reader) => VesselId = reader.ReadGuid();
    }

    /// <summary>Servidor → quien pilota la nave. Otro jugador pide el control.</summary>
    public sealed class VesselHandoverAskMessage : IMessage
    {
        public MessageType Type => MessageType.VesselHandoverAsk;

        public Guid VesselId;
        public int FromPlayerId;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteInt32(FromPlayerId);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            FromPlayerId = reader.ReadInt32();
        }
    }

    /// <summary>Cliente (quien pilota) → servidor. Le cede el control a otro jugador.</summary>
    public sealed class VesselHandoverGrantMessage : IMessage
    {
        public MessageType Type => MessageType.VesselHandoverGrant;

        public Guid VesselId;
        public int ToPlayerId;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteInt32(ToPlayerId);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            ToPlayerId = reader.ReadInt32();
        }
    }

    /// <summary>
    /// Ambos sentidos. Copiloto de una nave (0 = ninguno): sus mandos se envían a quien la pilota, que los aplica.
    /// Lo pide quien pilota; el servidor lo confirma a todos.
    /// </summary>
    public sealed class VesselCopilotMessage : IMessage
    {
        public MessageType Type => MessageType.VesselCopilot;

        public Guid VesselId;
        public int CopilotId;

        /// <summary>Si además puede accionar etapas, tren, luces y grupos de acción.</summary>
        public bool AllowActions;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteInt32(CopilotId);
            writer.WriteBool(AllowActions);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            CopilotId = reader.ReadInt32();
            AllowActions = reader.ReadBool();
        }
    }

    /// <summary>
    /// Copiloto → servidor → quien pilota, por UDP. Mandos de vuelo del copiloto, como los devuelve KSP:
    /// cada eje entre -1 y 1, y el acelerador entre 0 y 1.
    /// </summary>
    public sealed class VesselInputMessage : IMessage
    {
        public MessageType Type => MessageType.VesselInput;

        public Guid VesselId;
        public float Pitch;
        public float Yaw;
        public float Roll;
        public float Throttle;
        public float WheelSteer;
        public float WheelThrottle;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteSingle(Pitch);
            writer.WriteSingle(Yaw);
            writer.WriteSingle(Roll);
            writer.WriteSingle(Throttle);
            writer.WriteSingle(WheelSteer);
            writer.WriteSingle(WheelThrottle);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            Pitch = reader.ReadSingle();
            Yaw = reader.ReadSingle();
            Roll = reader.ReadSingle();
            Throttle = reader.ReadSingle();
            WheelSteer = reader.ReadSingle();
            WheelThrottle = reader.ReadSingle();
        }
    }

    /// <summary>Copiloto → servidor → quien pilota, por TCP (no se puede perder): etapa, tren, luces, grupos de acción...</summary>
    public sealed class VesselActionMessage : IMessage
    {
        public MessageType Type => MessageType.VesselAction;

        public Guid VesselId;
        public VesselAction Action;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteByte((byte)Action);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            Action = (VesselAction)reader.ReadByte();
        }
    }
}
