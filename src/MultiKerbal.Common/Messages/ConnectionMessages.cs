using MultiKerbal.Common.Mods;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>Cliente → servidor. Primer mensaje obligatorio de toda conexión.</summary>
    public sealed class HandshakeRequestMessage : IMessage
    {
        public MessageType Type => MessageType.HandshakeRequest;

        // ProtocolVersion va siempre primero para poder rechazar versiones distintas con un mensaje claro.
        public int ProtocolVersion;
        public string PlayerName;
        public string Password;
        public string ModVersion;
        public string GameVersion;

        /// <summary>Mods instalados en esta copia de KSP (carpetas de GameData).</summary>
        public ModInfo[] Mods = new ModInfo[0];

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(ProtocolVersion);
            writer.WriteString(PlayerName);
            writer.WriteString(Password);
            writer.WriteString(ModVersion);
            writer.WriteString(GameVersion);
            WriteMods(writer, Mods);
        }

        public void Read(PacketReader reader)
        {
            ProtocolVersion = reader.ReadInt32();
            PlayerName = reader.ReadString();
            Password = reader.ReadString();
            ModVersion = reader.ReadString();
            GameVersion = reader.ReadString();
            Mods = ReadMods(reader);
        }

        internal static void WriteMods(PacketWriter writer, ModInfo[] mods)
        {
            mods = mods ?? new ModInfo[0];
            writer.WriteInt32(mods.Length);
            foreach (ModInfo mod in mods)
                mod.Write(writer);
        }

        internal static ModInfo[] ReadMods(PacketReader reader)
        {
            int count = reader.ReadCount(ProtocolInfo.MaxMods);
            var mods = new ModInfo[count];
            for (int i = 0; i < count; i++)
                mods[i] = ModInfo.Read(reader);
            return mods;
        }
    }

    /// <summary>Servidor → cliente. Respuesta al saludo.</summary>
    public sealed class HandshakeResponseMessage : IMessage
    {
        public MessageType Type => MessageType.HandshakeResponse;

        public int ProtocolVersion;
        public bool Accepted;
        public string RejectReason;
        public int PlayerId;
        /// <summary>Identifica los datagramas UDP de este jugador.</summary>
        public ulong UdpToken;
        public string ServerName;
        public string Motd;

        /// <summary>Mods que espera el servidor. Va también cuando se rechaza: así se puede ver qué falta.</summary>
        public ModInfo[] Mods = new ModInfo[0];

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(ProtocolVersion);
            writer.WriteBool(Accepted);
            writer.WriteString(RejectReason);
            writer.WriteInt32(PlayerId);
            writer.WriteUInt64(UdpToken);
            writer.WriteString(ServerName);
            writer.WriteString(Motd);
            HandshakeRequestMessage.WriteMods(writer, Mods);
        }

        public void Read(PacketReader reader)
        {
            ProtocolVersion = reader.ReadInt32();
            Accepted = reader.ReadBool();
            RejectReason = reader.ReadString();
            PlayerId = reader.ReadInt32();
            UdpToken = reader.ReadUInt64();
            ServerName = reader.ReadString();
            Motd = reader.ReadString();
            Mods = HandshakeRequestMessage.ReadMods(reader);
        }
    }

    /// <summary>Ambos sentidos. Aviso de cierre ordenado con motivo.</summary>
    public sealed class DisconnectMessage : IMessage
    {
        public MessageType Type => MessageType.Disconnect;

        public string Reason;

        public void Write(PacketWriter writer) => writer.WriteString(Reason);

        public void Read(PacketReader reader) => Reason = reader.ReadString();
    }

    /// <summary>Cliente → servidor. Mide latencia y sincroniza relojes.</summary>
    public sealed class PingMessage : IMessage
    {
        public MessageType Type => MessageType.Ping;

        /// <summary>Reloj monotónico local del cliente, en segundos.</summary>
        public double ClientTime;

        public void Write(PacketWriter writer) => writer.WriteDouble(ClientTime);

        public void Read(PacketReader reader) => ClientTime = reader.ReadDouble();
    }

    /// <summary>Servidor → cliente. Respuesta inmediata a un ping.</summary>
    public sealed class PongMessage : IMessage
    {
        public MessageType Type => MessageType.Pong;

        public double ClientTime;
        /// <summary>Reloj monotónico del servidor al recibir el ping, en segundos.</summary>
        public double ServerTime;

        public void Write(PacketWriter writer)
        {
            writer.WriteDouble(ClientTime);
            writer.WriteDouble(ServerTime);
        }

        public void Read(PacketReader reader)
        {
            ClientTime = reader.ReadDouble();
            ServerTime = reader.ReadDouble();
        }
    }

    /// <summary>Cliente → servidor por UDP. Registra el endpoint UDP (el token va en la cabecera del datagrama).</summary>
    public sealed class UdpBindMessage : IMessage
    {
        public MessageType Type => MessageType.UdpBind;

        public void Write(PacketWriter writer)
        {
        }

        public void Read(PacketReader reader)
        {
        }
    }

    /// <summary>Servidor → cliente por UDP. Confirma que el canal UDP funciona.</summary>
    public sealed class UdpBindAckMessage : IMessage
    {
        public MessageType Type => MessageType.UdpBindAck;

        public void Write(PacketWriter writer)
        {
        }

        public void Read(PacketReader reader)
        {
        }
    }
}
