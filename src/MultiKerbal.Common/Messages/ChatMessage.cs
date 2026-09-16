using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    /// <summary>Cliente → servidor (SenderId ignorado) y servidor → clientes.</summary>
    public sealed class ChatMessage : IMessage
    {
        /// <summary>Remitente de los avisos del propio servidor. Los jugadores empiezan en 1.</summary>
        public const int SystemSenderId = 0;

        public MessageType Type => MessageType.Chat;

        public int SenderId;
        public string Text;

        public void Write(PacketWriter writer)
        {
            writer.WriteInt32(SenderId);
            writer.WriteString(Text);
        }

        public void Read(PacketReader reader)
        {
            SenderId = reader.ReadInt32();
            Text = reader.ReadString();
        }
    }
}
