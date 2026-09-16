using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    public enum PlayerActivity : byte
    {
        Loading = 0,
        MainMenu = 1,
        SpaceCenter = 2,
        Editor = 3,
        Flight = 4,
        TrackingStation = 5,
    }

    public sealed class PlayerInfo
    {
        public int Id;
        public string Name;
        /// <summary>Tono (0..1) para etiquetas y marcadores del jugador.</summary>
        public float ColorHue;
        public PlayerActivity Activity;
        /// <summary>Texto libre, p. ej. el nombre de la nave que pilota.</summary>
        public string Detail;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(Id);
            writer.WriteString(Name);
            writer.WriteSingle(ColorHue);
            writer.WriteByte((byte)Activity);
            writer.WriteString(Detail);
        }

        public static PlayerInfo Read(PacketReader reader)
        {
            return new PlayerInfo
            {
                Id = reader.ReadInt32(),
                Name = reader.ReadString(),
                ColorHue = reader.ReadSingle(),
                Activity = (PlayerActivity)reader.ReadByte(),
                Detail = reader.ReadString(),
            };
        }
    }

    /// <summary>Servidor → cliente. Lista completa de jugadores (incluido el receptor).</summary>
    public sealed class PlayerListMessage : IMessage
    {
        public MessageType Type => MessageType.PlayerList;

        public PlayerInfo[] Players = new PlayerInfo[0];

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(Players.Length);
            foreach (PlayerInfo player in Players)
                player.Write(writer);
        }

        public void Read(PacketReader reader)
        {
            int count = reader.ReadCount(ProtocolInfo.MaxPlayers);
            Players = new PlayerInfo[count];
            for (int i = 0; i < count; i++)
                Players[i] = PlayerInfo.Read(reader);
        }
    }

    /// <summary>Servidor → cliente.</summary>
    public sealed class PlayerJoinedMessage : IMessage
    {
        public MessageType Type => MessageType.PlayerJoined;

        public PlayerInfo Player = new PlayerInfo();

        public void Write(PacketWriter writer) => Player.Write(writer);

        public void Read(PacketReader reader) => Player = PlayerInfo.Read(reader);
    }

    /// <summary>Servidor → cliente.</summary>
    public sealed class PlayerLeftMessage : IMessage
    {
        public MessageType Type => MessageType.PlayerLeft;

        public int PlayerId;

        public void Write(PacketWriter writer) => writer.WriteInt32(PlayerId);

        public void Read(PacketReader reader) => PlayerId = reader.ReadInt32();
    }

    /// <summary>Cliente → servidor (PlayerId ignorado) y servidor → clientes (PlayerId del jugador que cambió).</summary>
    public sealed class PlayerStatusMessage : IMessage
    {
        public MessageType Type => MessageType.PlayerStatus;

        public int PlayerId;
        public PlayerActivity Activity;
        public string Detail;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(PlayerId);
            writer.WriteByte((byte)Activity);
            writer.WriteString(Detail);
        }

        public void Read(PacketReader reader)
        {
            PlayerId = reader.ReadInt32();
            Activity = (PlayerActivity)reader.ReadByte();
            Detail = reader.ReadString();
        }
    }
}
