using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>Estado del enlace del servidor con el relay.</summary>
    public enum RelayLinkState : byte
    {
        /// <summary>El servidor no usa el relay.</summary>
        Off = 0,

        /// <summary>Intentando registrarse (o reintentando tras perder el relay).</summary>
        Connecting = 1,

        /// <summary>Registrado: ya tiene código.</summary>
        Registered = 2,

        /// <summary>El relay ha rechazado el registro (el motivo va en el estado).</summary>
        Rejected = 3,
    }

    /// <summary>Orden que el anfitrión da al servidor sobre su presencia en el relay.</summary>
    public enum ServerControlAction : byte
    {
        /// <summary>Volver a comprobar si los puertos son alcanzables desde fuera.</summary>
        Recheck = 0,

        /// <summary>Aparecer en la lista pública.</summary>
        SetPublic = 1,

        /// <summary>Dejar la lista pública (se sigue entrando con el código).</summary>
        SetPrivate = 2,
    }

    /// <summary>Mod → servidor. Pide el estado del servidor (sin necesidad de entrar a la partida).</summary>
    public sealed class ServerStatusRequestMessage : IMessage
    {
        public MessageType Type => MessageType.ServerStatusRequest;

        public void Write(PacketWriter writer)
        {
        }

        public void Read(PacketReader reader)
        {
        }
    }

    /// <summary>Servidor → mod. Estado actual del servidor y de su enlace con el relay.</summary>
    public sealed class ServerStatusResponseMessage : IMessage
    {
        public MessageType Type => MessageType.ServerStatusResponse;

        // GameProtocol va siempre primero, por la misma razón que ProtocolVersion en el saludo.
        public int GameProtocol;
        public string ModVersion;
        public string Name;
        public int Players;
        public int MaxPlayers;
        public bool HasPassword;
        public RelayLinkState RelayState;

        /// <summary>Motivo del rechazo o del fallo del relay; vacío si no hay ninguno.</summary>
        public string RelayError;

        /// <summary>Código para entrar (vacío mientras no esté registrado).</summary>
        public string Code;

        /// <summary>Los puertos son alcanzables desde fuera: se puede entrar sin pasar por el relay.</summary>
        public bool Direct;

        public bool IsPublic;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(GameProtocol);
            writer.WriteString(ModVersion);
            writer.WriteString(Name);
            writer.WriteInt32(Players);
            writer.WriteInt32(MaxPlayers);
            writer.WriteBool(HasPassword);
            writer.WriteByte((byte)RelayState);
            writer.WriteString(RelayError);
            writer.WriteString(Code);
            writer.WriteBool(Direct);
            writer.WriteBool(IsPublic);
        }

        public void Read(PacketReader reader)
        {
            GameProtocol = reader.ReadInt32();
            ModVersion = reader.ReadString();
            Name = reader.ReadString();
            Players = reader.ReadInt32();
            MaxPlayers = reader.ReadInt32();
            HasPassword = reader.ReadBool();
            RelayState = (RelayLinkState)reader.ReadByte();
            RelayError = reader.ReadString();
            Code = reader.ReadString();
            Direct = reader.ReadBool();
            IsPublic = reader.ReadBool();
        }
    }

    /// <summary>Mod → servidor. Orden del anfitrión.</summary>
    public sealed class ServerControlMessage : IMessage
    {
        public MessageType Type => MessageType.ServerControl;

        public ServerControlAction Action;

        public void Write(PacketWriter writer) => writer.WriteByte((byte)Action);

        public void Read(PacketReader reader) => Action = (ServerControlAction)reader.ReadByte();
    }

    /// <summary>Servidor → mod. Código del servidor, avisado en cuanto cambia.</summary>
    public sealed class ServerCodeMessage : IMessage
    {
        public MessageType Type => MessageType.ServerCode;

        public string Code;
        public bool Direct;

        public void Write(PacketWriter writer)
        {
            writer.WriteString(Code);
            writer.WriteBool(Direct);
        }

        public void Read(PacketReader reader)
        {
            Code = reader.ReadString();
            Direct = reader.ReadBool();
        }
    }
}
