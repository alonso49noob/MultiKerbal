using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>Datos de un servidor que el relay muestra en la lista y comprueba al entrar.</summary>
    public sealed class RelayServerInfo
    {
        public string Name;
        public int Players;
        public int MaxPlayers;
        public bool HasPassword;

        /// <summary>El anfitrión quiere salir en la lista pública.</summary>
        public bool IsPublic;

        /// <summary>Versión del protocolo del juego (<see cref="ProtocolInfo.Version"/>).</summary>
        public int GameProtocol;

        public string ModVersion;

        public void Write(PacketWriter writer)
        {
            writer.WriteString(Name);
            writer.WriteInt32(Players);
            writer.WriteInt32(MaxPlayers);
            writer.WriteBool(HasPassword);
            writer.WriteBool(IsPublic);
            writer.WriteInt32(GameProtocol);
            writer.WriteString(ModVersion);
        }

        public static RelayServerInfo Read(PacketReader reader)
        {
            return new RelayServerInfo
            {
                Name = reader.ReadString(),
                Players = reader.ReadInt32(),
                MaxPlayers = reader.ReadInt32(),
                HasPassword = reader.ReadBool(),
                IsPublic = reader.ReadBool(),
                GameProtocol = reader.ReadInt32(),
                ModVersion = reader.ReadString(),
            };
        }
    }

    /// <summary>Una fila de la lista pública de servidores.</summary>
    public sealed class PublicServerEntry
    {
        public string Code;
        public string Name;
        public int Players;
        public int MaxPlayers;
        public bool HasPassword;
        public int GameProtocol;
        public string ModVersion;

        /// <summary>Se puede entrar directamente (sin pasar por el relay).</summary>
        public bool Direct;

        public void Write(PacketWriter writer)
        {
            writer.WriteString(Code);
            writer.WriteString(Name);
            writer.WriteInt32(Players);
            writer.WriteInt32(MaxPlayers);
            writer.WriteBool(HasPassword);
            writer.WriteInt32(GameProtocol);
            writer.WriteString(ModVersion);
            writer.WriteBool(Direct);
        }

        public static PublicServerEntry Read(PacketReader reader)
        {
            return new PublicServerEntry
            {
                Code = reader.ReadString(),
                Name = reader.ReadString(),
                Players = reader.ReadInt32(),
                MaxPlayers = reader.ReadInt32(),
                HasPassword = reader.ReadBool(),
                GameProtocol = reader.ReadInt32(),
                ModVersion = reader.ReadString(),
                Direct = reader.ReadBool(),
            };
        }
    }

    /// <summary>Servidor → relay. Primer mensaje: pide un código y empieza a figurar.</summary>
    public sealed class RelayRegisterMessage : IMessage
    {
        /// <summary>Tamaño exacto de la clave del servidor.</summary>
        public const int KeyLength = 32;

        public MessageType Type => MessageType.RelayRegister;

        // RelayVersion va siempre primero para poder rechazar versiones distintas con un mensaje claro.
        public int RelayVersion;

        /// <summary>Clave secreta del servidor: la misma clave conserva el mismo código.</summary>
        public byte[] Key = new byte[KeyLength];

        /// <summary>Puerto del juego en el servidor, el que el relay comprueba desde fuera.</summary>
        public int GamePort;

        public RelayServerInfo Info = new RelayServerInfo();

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(RelayVersion);
            writer.WriteBytes(Key);
            writer.WriteInt32(GamePort);
            Info.Write(writer);
        }

        public void Read(PacketReader reader)
        {
            RelayVersion = reader.ReadInt32();
            Key = reader.ReadBytes();
            if (Key == null || Key.Length != KeyLength)
                throw new ProtocolException("Clave del servidor inválida");
            GamePort = reader.ReadInt32();
            Info = RelayServerInfo.Read(reader);
        }
    }

    /// <summary>Relay → servidor. Registro aceptado.</summary>
    public sealed class RelayRegisteredMessage : IMessage
    {
        public MessageType Type => MessageType.RelayRegistered;

        public string Code;

        /// <summary>Secreto con el que el servidor se identifica en sus hola UDP.</summary>
        public ulong UdpSecret;

        public void Write(PacketWriter writer)
        {
            writer.WriteString(Code);
            writer.WriteUInt64(UdpSecret);
        }

        public void Read(PacketReader reader)
        {
            Code = reader.ReadString();
            UdpSecret = reader.ReadUInt64();
        }
    }

    /// <summary>Relay → servidor. Registro rechazado, con el motivo.</summary>
    public sealed class RelayRejectedMessage : IMessage
    {
        public MessageType Type => MessageType.RelayRejected;

        public string Reason;

        public void Write(PacketWriter writer) => writer.WriteString(Reason);

        public void Read(PacketReader reader) => Reason = reader.ReadString();
    }

    /// <summary>Servidor → relay. Latido periódico con los datos al día.</summary>
    public sealed class RelayHeartbeatMessage : IMessage
    {
        public MessageType Type => MessageType.RelayHeartbeat;

        public RelayServerInfo Info = new RelayServerInfo();

        public void Write(PacketWriter writer) => Info.Write(writer);

        public void Read(PacketReader reader) => Info = RelayServerInfo.Read(reader);
    }

    /// <summary>Relay → servidor. Resultado de comprobar los puertos desde fuera.</summary>
    public sealed class RelayReachabilityMessage : IMessage
    {
        public MessageType Type => MessageType.RelayReachability;

        public bool Tcp;
        public bool Udp;

        public void Write(PacketWriter writer)
        {
            writer.WriteBool(Tcp);
            writer.WriteBool(Udp);
        }

        public void Read(PacketReader reader)
        {
            Tcp = reader.ReadBool();
            Udp = reader.ReadBool();
        }
    }

    /// <summary>Servidor → relay. Pide repetir la comprobación de puertos.</summary>
    public sealed class RelayRecheckMessage : IMessage
    {
        public MessageType Type => MessageType.RelayRecheck;

        public void Write(PacketWriter writer)
        {
        }

        public void Read(PacketReader reader)
        {
        }
    }

    /// <summary>Relay → servidor. Un jugador quiere entrar: el servidor abre una conexión de enganche.</summary>
    public sealed class RelayIncomingMessage : IMessage
    {
        public MessageType Type => MessageType.RelayIncoming;

        public long SessionId;

        /// <summary>Secreto de esta sesión; el servidor lo devuelve en <see cref="RelayAttachMessage"/>.</summary>
        public ulong Secret;

        /// <summary>Dirección del jugador, para el registro del servidor.</summary>
        public string ClientAddress;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt64(SessionId);
            writer.WriteUInt64(Secret);
            writer.WriteString(ClientAddress);
        }

        public void Read(PacketReader reader)
        {
            SessionId = reader.ReadInt64();
            Secret = reader.ReadUInt64();
            ClientAddress = reader.ReadString();
        }
    }

    /// <summary>Servidor → relay. Primer mensaje de la conexión de enganche de una sesión.</summary>
    public sealed class RelayAttachMessage : IMessage
    {
        public MessageType Type => MessageType.RelayAttach;

        public int RelayVersion;
        public long SessionId;
        public ulong Secret;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(RelayVersion);
            writer.WriteInt64(SessionId);
            writer.WriteUInt64(Secret);
        }

        public void Read(PacketReader reader)
        {
            RelayVersion = reader.ReadInt32();
            SessionId = reader.ReadInt64();
            Secret = reader.ReadUInt64();
        }
    }

    /// <summary>Servidor → relay por UDP. Mantiene abierto el puerto UDP del servidor en su router.</summary>
    public sealed class RelayUdpHelloMessage : IMessage
    {
        public MessageType Type => MessageType.RelayUdpHello;

        public ulong UdpSecret;

        public void Write(PacketWriter writer) => writer.WriteUInt64(UdpSecret);

        public void Read(PacketReader reader) => UdpSecret = reader.ReadUInt64();
    }

    /// <summary>Servidor → relay. Ficha UDP que el servidor ha dado al jugador de una sesión del relay.</summary>
    public sealed class RelayUdpTokenMessage : IMessage
    {
        public MessageType Type => MessageType.RelayUdpToken;

        public long SessionId;
        public ulong Token;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt64(SessionId);
            writer.WriteUInt64(Token);
        }

        public void Read(PacketReader reader)
        {
            SessionId = reader.ReadInt64();
            Token = reader.ReadUInt64();
        }
    }

    /// <summary>Relay → servidor, por TCP (primera trama) o por UDP (ficha 0). Sonda de la comprobación de acceso.</summary>
    public sealed class RelayProbeMessage : IMessage
    {
        public MessageType Type => MessageType.RelayProbe;

        public ulong Nonce;

        public void Write(PacketWriter writer) => writer.WriteUInt64(Nonce);

        public void Read(PacketReader reader) => Nonce = reader.ReadUInt64();
    }

    /// <summary>Servidor → relay, por donde llegó la sonda. Respuesta con el mismo nonce.</summary>
    public sealed class RelayProbeReplyMessage : IMessage
    {
        public MessageType Type => MessageType.RelayProbeReply;

        public ulong Nonce;

        public void Write(PacketWriter writer) => writer.WriteUInt64(Nonce);

        public void Read(PacketReader reader) => Nonce = reader.ReadUInt64();
    }

    /// <summary>Jugador → relay. Busca un servidor por su código.</summary>
    public sealed class RelayLookupMessage : IMessage
    {
        public MessageType Type => MessageType.RelayLookup;

        public int RelayVersion;
        public string Code;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(RelayVersion);
            writer.WriteString(Code);
        }

        public void Read(PacketReader reader)
        {
            RelayVersion = reader.ReadInt32();
            Code = reader.ReadString();
        }
    }

    /// <summary>Relay → jugador. Datos del servidor buscado, o el motivo de no encontrarlo.</summary>
    public sealed class RelayLookupResultMessage : IMessage
    {
        public MessageType Type => MessageType.RelayLookupResult;

        public bool Found;
        public string Name;
        public int GameProtocol;
        public string ModVersion;

        /// <summary>Dirección directa del servidor si es alcanzable; vacía si hay que pasar por el relay.</summary>
        public string DirectHost;

        public int DirectPort;
        public string Error;

        public void Write(PacketWriter writer)
        {
            writer.WriteBool(Found);
            writer.WriteString(Name);
            writer.WriteInt32(GameProtocol);
            writer.WriteString(ModVersion);
            writer.WriteString(DirectHost);
            writer.WriteInt32(DirectPort);
            writer.WriteString(Error);
        }

        public void Read(PacketReader reader)
        {
            Found = reader.ReadBool();
            Name = reader.ReadString();
            GameProtocol = reader.ReadInt32();
            ModVersion = reader.ReadString();
            DirectHost = reader.ReadString();
            DirectPort = reader.ReadInt32();
            Error = reader.ReadString();
        }
    }

    /// <summary>Jugador → relay. Pide que el relay lo conecte con el servidor de ese código.</summary>
    public sealed class RelayJoinMessage : IMessage
    {
        public MessageType Type => MessageType.RelayJoin;

        public int RelayVersion;
        public string Code;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(RelayVersion);
            writer.WriteString(Code);
        }

        public void Read(PacketReader reader)
        {
            RelayVersion = reader.ReadInt32();
            Code = reader.ReadString();
        }
    }

    /// <summary>Relay → jugador. Si <see cref="Ok"/>, a partir de aquí la conexión es el juego.</summary>
    public sealed class RelayJoinResultMessage : IMessage
    {
        public MessageType Type => MessageType.RelayJoinResult;

        public bool Ok;
        public string Error;

        public void Write(PacketWriter writer)
        {
            writer.WriteBool(Ok);
            writer.WriteString(Error);
        }

        public void Read(PacketReader reader)
        {
            Ok = reader.ReadBool();
            Error = reader.ReadString();
        }
    }

    /// <summary>Jugador → relay. Pide la lista pública de servidores.</summary>
    public sealed class RelayListRequestMessage : IMessage
    {
        public MessageType Type => MessageType.RelayListRequest;

        public int RelayVersion;

        public void Write(PacketWriter writer) => writer.WriteInt32(RelayVersion);

        public void Read(PacketReader reader) => RelayVersion = reader.ReadInt32();
    }

    /// <summary>Relay → jugador. Servidores públicos (como mucho <see cref="ProtocolInfo.MaxListedServers"/>).</summary>
    public sealed class RelayListResponseMessage : IMessage
    {
        public MessageType Type => MessageType.RelayListResponse;

        public PublicServerEntry[] Servers = new PublicServerEntry[0];

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(Servers.Length);
            foreach (PublicServerEntry server in Servers)
                server.Write(writer);
        }

        public void Read(PacketReader reader)
        {
            int count = reader.ReadCount(ProtocolInfo.MaxListedServers);
            Servers = new PublicServerEntry[count];
            for (int i = 0; i < count; i++)
                Servers[i] = PublicServerEntry.Read(reader);
        }
    }
}
