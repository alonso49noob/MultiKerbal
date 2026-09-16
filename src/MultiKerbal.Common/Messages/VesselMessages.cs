using System;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>
    /// Ambos sentidos. Definición completa de una nave: nodo VESSEL de KSP más su tripulación, en texto UTF-8
    /// comprimido con GZip. Se envía al crearla y cuando cambia su estructura (piezas, tripulación, nombre).
    /// </summary>
    public sealed class VesselProtoMessage : IMessage
    {
        public MessageType Type => MessageType.VesselProto;

        public Guid VesselId;

        /// <summary>Servidor → cliente: jugador que la controla (0 = nadie). El servidor ignora el valor del cliente.</summary>
        public int OwnerId;

        public string VesselName;

        /// <summary>
        /// Cambia solo cuando cambia la estructura (piezas, tripulación, nombre). Un reenvío con el mismo valor
        /// actualiza los datos guardados pero no obliga a los demás a recargar la nave.
        /// </summary>
        public int StructureVersion;

        public byte[] Data;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteInt32(OwnerId);
            writer.WriteString(VesselName);
            writer.WriteInt32(StructureVersion);
            writer.WriteBytes(Data);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            OwnerId = reader.ReadInt32();
            VesselName = reader.ReadString();
            StructureVersion = reader.ReadInt32();
            Data = reader.ReadBytes();
        }
    }

    /// <summary>
    /// Ambos sentidos, normalmente por UDP. Estado de una nave en el instante <see cref="UniversalTime"/>.
    /// La posición va como elementos orbitales, que no dependen del marco de referencia local de cada jugador
    /// y se pueden propagar en el tiempo, y como latitud/longitud/altitud para las naves posadas.
    /// La rotación es relativa al cuerpo de referencia (srfRelRotation de KSP).
    /// </summary>
    public sealed class VesselUpdateMessage : IMessage
    {
        /// <summary>LANDED | SPLASHED | PRELAUNCH en Vessel.Situations de KSP.</summary>
        public const int SurfaceSituations = 1 | 2 | 4;

        public MessageType Type => MessageType.VesselUpdate;

        public Guid VesselId;
        public double UniversalTime;

        /// <summary>flightGlobalsIndex del cuerpo de referencia.</summary>
        public int BodyIndex;

        /// <summary>Vessel.Situations de KSP.</summary>
        public int Situation;

        public double Latitude;
        public double Longitude;
        public double Altitude;

        public double Inclination;
        public double Eccentricity;
        public double SemiMajorAxis;
        public double LongitudeOfAscendingNode;
        public double ArgumentOfPeriapsis;
        public double MeanAnomalyAtEpoch;
        public double Epoch;

        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public bool IsOnSurface => (Situation & SurfaceSituations) != 0;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteDouble(UniversalTime);
            writer.WriteInt32(BodyIndex);
            writer.WriteInt32(Situation);
            writer.WriteDouble(Latitude);
            writer.WriteDouble(Longitude);
            writer.WriteDouble(Altitude);
            writer.WriteDouble(Inclination);
            writer.WriteDouble(Eccentricity);
            writer.WriteDouble(SemiMajorAxis);
            writer.WriteDouble(LongitudeOfAscendingNode);
            writer.WriteDouble(ArgumentOfPeriapsis);
            writer.WriteDouble(MeanAnomalyAtEpoch);
            writer.WriteDouble(Epoch);
            writer.WriteSingle(RotationX);
            writer.WriteSingle(RotationY);
            writer.WriteSingle(RotationZ);
            writer.WriteSingle(RotationW);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            UniversalTime = reader.ReadDouble();
            BodyIndex = reader.ReadInt32();
            Situation = reader.ReadInt32();
            Latitude = reader.ReadDouble();
            Longitude = reader.ReadDouble();
            Altitude = reader.ReadDouble();
            Inclination = reader.ReadDouble();
            Eccentricity = reader.ReadDouble();
            SemiMajorAxis = reader.ReadDouble();
            LongitudeOfAscendingNode = reader.ReadDouble();
            ArgumentOfPeriapsis = reader.ReadDouble();
            MeanAnomalyAtEpoch = reader.ReadDouble();
            Epoch = reader.ReadDouble();
            RotationX = reader.ReadSingle();
            RotationY = reader.ReadSingle();
            RotationZ = reader.ReadSingle();
            RotationW = reader.ReadSingle();
        }
    }

    /// <summary>Ambos sentidos. La nave dejó de existir (destruida, recuperada, acoplada a otra...).</summary>
    public sealed class VesselRemoveMessage : IMessage
    {
        public MessageType Type => MessageType.VesselRemove;

        public Guid VesselId;

        public void Write(PacketWriter writer) => writer.WriteGuid(VesselId);

        public void Read(PacketReader reader) => VesselId = reader.ReadGuid();
    }

    /// <summary>Servidor → cliente. Quién controla la nave (0 = nadie).</summary>
    public sealed class VesselOwnershipMessage : IMessage
    {
        public MessageType Type => MessageType.VesselOwnership;

        public Guid VesselId;
        public int OwnerId;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteInt32(OwnerId);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            OwnerId = reader.ReadInt32();
        }
    }

    /// <summary>Cliente → servidor. Tomar (si no tiene dueño) o soltar el control de una nave.</summary>
    public sealed class VesselOwnershipRequestMessage : IMessage
    {
        public MessageType Type => MessageType.VesselOwnershipRequest;

        public Guid VesselId;
        public bool Acquire;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteBool(Acquire);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            Acquire = reader.ReadBool();
        }
    }
}
