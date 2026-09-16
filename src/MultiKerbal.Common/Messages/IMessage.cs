using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Messages
{
    public interface IMessage
    {
        MessageType Type { get; }

        void Write(PacketWriter writer);

        void Read(PacketReader reader);
    }
}
