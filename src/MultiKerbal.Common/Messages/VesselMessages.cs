using System;
using MultiKerbal.Common.Serialization;
using MultiKerbal.Common.Vessels;

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

        /// <summary>Servidor → cliente: jugador que la pilota ahora (0 = nadie). El servidor ignora el valor del cliente.</summary>
        public int ControllerId;

        /// <summary>Nombre del dueño (vacío = sin dueño). Del cliente solo se usa al crear la nave: pasa a ser de quien la publica.</summary>
        public string OwnerName;

        /// <summary>Del cliente solo se usa al crear la nave (su acceso por defecto).</summary>
        public VesselAccess Access;

        /// <summary>Servidor → cliente: jugador al que el dueño se la ha prestado (vacío = a nadie). El servidor ignora el del cliente.</summary>
        public string LentTo;

        public string VesselName;

        /// <summary>Nombres de los kerbals a bordo. El servidor no deja que un kerbal esté en naves de dos jugadores.</summary>
        public string[] Crew = new string[0];

        /// <summary>
        /// Cliente → servidor, solo al crearla: la nave de la que se separó (etapa, desacople, EVA). Si era de otro
        /// jugador, la nueva sigue siendo de ese dueño. Vacío si no salió de otra.
        /// </summary>
        public Guid ParentId;

        /// <summary>Servidor → cliente: su dueño puede devolverla a como la dejó (ver <see cref="VesselRestoreRequestMessage"/>).</summary>
        public bool Restorable;

        /// <summary>
        /// Cambia solo cuando cambia la estructura (piezas, tripulación, nombre). Un reenvío con el mismo valor
        /// actualiza los datos guardados pero no obliga a los demás a recargar la nave.
        /// </summary>
        public int StructureVersion;

        public byte[] Data;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteInt32(ControllerId);
            writer.WriteString(OwnerName);
            writer.WriteByte((byte)Access);
            writer.WriteString(LentTo);
            writer.WriteString(VesselName);
            writer.WriteStrings(Crew);
            writer.WriteGuid(ParentId);
            writer.WriteBool(Restorable);
            writer.WriteInt32(StructureVersion);
            writer.WriteBytes(Data);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            ControllerId = reader.ReadInt32();
            OwnerName = reader.ReadString();
            Access = (VesselAccess)reader.ReadByte();
            LentTo = reader.ReadString();
            VesselName = reader.ReadString();
            Crew = reader.ReadStrings(ProtocolInfo.MaxCrewPerVessel);
            ParentId = reader.ReadGuid();
            Restorable = reader.ReadBool();
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

    /// <summary>Por qué desaparece una nave: decide si hace falta permiso del dueño.</summary>
    public enum VesselRemoveReason : byte
    {
        /// <summary>Destruida en la partida de quien la pilota (choque, calor, fuera de alcance en la atmósfera...).</summary>
        Destroyed = 0,

        /// <summary>Recuperada (vuelve al Centro Espacial con su tripulación).</summary>
        Recovered = 1,

        /// <summary>Borrada desde la estación de seguimiento.</summary>
        Deleted = 2,

        /// <summary>Fundida en otra nave: acoplamiento, garra o un kerbal que embarca. No hace falta permiso.</summary>
        Merged = 3,
    }

    /// <summary>
    /// Ambos sentidos. La nave dejó de existir. Destruirla, recuperarla o borrarla necesita permiso del dueño
    /// (ver <see cref="VesselPermissions.CanRemove"/>); si no lo hay, el servidor la mantiene y la devuelve.
    /// </summary>
    public sealed class VesselRemoveMessage : IMessage
    {
        public MessageType Type => MessageType.VesselRemove;

        public Guid VesselId;

        /// <summary>Del cliente al servidor; del servidor a los demás va tal cual.</summary>
        public VesselRemoveReason Reason;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteByte((byte)Reason);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            Reason = (VesselRemoveReason)reader.ReadByte();
        }
    }

    /// <summary>
    /// Servidor → cliente. No se acepta una nave nueva que acaba de publicar (p. ej. lleva un kerbal que ya va en la
    /// nave de otro jugador). El cliente la quita de su partida.
    /// </summary>
    public sealed class VesselRejectMessage : IMessage
    {
        public MessageType Type => MessageType.VesselReject;

        public Guid VesselId;
        public string Reason;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteString(Reason);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            Reason = reader.ReadString();
        }
    }

    /// <summary>Servidor → cliente. Quién pilota la nave (0 = nadie). También responde a una petición denegada.</summary>
    public sealed class VesselControlMessage : IMessage
    {
        public MessageType Type => MessageType.VesselControl;

        public Guid VesselId;
        public int ControllerId;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteInt32(ControllerId);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            ControllerId = reader.ReadInt32();
        }
    }

    /// <summary>Cliente → servidor. Tomar (si nadie la pilota y el acceso lo permite) o soltar el control de una nave.</summary>
    public sealed class VesselControlRequestMessage : IMessage
    {
        public MessageType Type => MessageType.VesselControlRequest;

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

    /// <summary>Servidor → cliente. Dueño, acceso y préstamo de una nave tras un cambio (o la respuesta a una petición denegada).</summary>
    public sealed class VesselOwnerMessage : IMessage
    {
        public MessageType Type => MessageType.VesselOwner;

        public Guid VesselId;

        /// <summary>Vacío = sin dueño.</summary>
        public string OwnerName;

        public VesselAccess Access;

        /// <summary>Jugador al que el dueño se la ha prestado (vacío = a nadie).</summary>
        public string LentTo;

        /// <summary>El dueño puede devolverla a como la dejó (la ha usado alguien sin permiso para destruirla).</summary>
        public bool Restorable;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteString(OwnerName);
            writer.WriteByte((byte)Access);
            writer.WriteString(LentTo);
            writer.WriteBool(Restorable);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            OwnerName = reader.ReadString();
            Access = (VesselAccess)reader.ReadByte();
            LentTo = reader.ReadString();
            Restorable = reader.ReadBool();
        }
    }

    /// <summary>
    /// Cliente → servidor. El dueño devuelve su nave a como la dejó antes de que la usara alguien sin permiso para
    /// destruirla (por ejemplo, si se la devuelven sin piezas). Solo si nadie más la está pilotando.
    /// </summary>
    public sealed class VesselRestoreRequestMessage : IMessage
    {
        public MessageType Type => MessageType.VesselRestoreRequest;

        public Guid VesselId;

        public void Write(PacketWriter writer) => writer.WriteGuid(VesselId);

        public void Read(PacketReader reader) => VesselId = reader.ReadGuid();
    }

    /// <summary>
    /// Cliente → servidor. Prestar una nave a un jugador conectado, o terminar el préstamo (nombre vacío).
    /// <list type="bullet">
    /// <item>El dueño la presta (aunque sea privada: el préstamo le deja pilotarla) o la recupera; al recuperarla,
    /// si quien la tenía prestada la está pilotando, deja de hacerlo.</item>
    /// <item>Quien la tiene prestada puede devolverla.</item>
    /// </list>
    /// Un kerbal en EVA es una nave más: se presta igual.
    /// </summary>
    public sealed class VesselLendRequestMessage : IMessage
    {
        public MessageType Type => MessageType.VesselLendRequest;

        public Guid VesselId;

        /// <summary>Vacío = recuperarla (el dueño) o devolverla (quien la tiene prestada).</summary>
        public string PlayerName;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteString(PlayerName);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            PlayerName = reader.ReadString();
        }
    }

    /// <summary>
    /// Cliente → servidor. Cambiar el dueño y el acceso de una nave. Solo puede pedirlo su dueño o, si no tiene,
    /// cualquiera que la reclame para sí. Nuevo dueño: uno mismo, otro jugador conectado (regalo) o vacío (sin dueño).
    /// </summary>
    public sealed class VesselOwnerRequestMessage : IMessage
    {
        public MessageType Type => MessageType.VesselOwnerRequest;

        public Guid VesselId;
        public string OwnerName;
        public VesselAccess Access;

        public void Write(PacketWriter writer)
        {
            writer.WriteGuid(VesselId);
            writer.WriteString(OwnerName);
            writer.WriteByte((byte)Access);
        }

        public void Read(PacketReader reader)
        {
            VesselId = reader.ReadGuid();
            OwnerName = reader.ReadString();
            Access = (VesselAccess)reader.ReadByte();
        }
    }
}
