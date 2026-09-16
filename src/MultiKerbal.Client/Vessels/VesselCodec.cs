using System.IO;
using System.Text;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Client.Vessels
{
    /// <summary>Nave ↔ bytes: nodo VESSEL de KSP más su tripulación, en texto comprimido.</summary>
    internal static class VesselCodec
    {
        private const string RootNodeName = "MULTIKERBAL_VESSEL";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static byte[] Encode(Vessel vessel)
        {
            ProtoVessel proto = vessel.BackupVessel();
            var root = new ConfigNode(RootNodeName);
            proto.Save(root.AddNode("VESSEL"));

            // La tripulación va incluida para que el receptor pueda crear los kerbals que no tenga en su plantel.
            foreach (ProtoCrewMember crew in proto.GetVesselCrew())
                crew.Save(root.AddNode("KERBAL"));

            return Compression.Compress(Utf8.GetBytes(root.ToString()));
        }

        public static DecodedVessel Decode(byte[] data)
        {
            string text = Utf8.GetString(Compression.Decompress(data));
            ConfigNode parsed = ConfigNode.Parse(text);
            ConfigNode root = parsed?.GetNode(RootNodeName) ?? parsed;
            ConfigNode vesselNode = root?.GetNode("VESSEL");
            if (vesselNode == null)
                throw new InvalidDataException("los datos no contienen una nave");

            return new DecodedVessel(vesselNode, root.GetNodes("KERBAL"));
        }
    }

    internal sealed class DecodedVessel
    {
        public DecodedVessel(ConfigNode vesselNode, ConfigNode[] crewNodes)
        {
            VesselNode = vesselNode;
            CrewNodes = crewNodes;
        }

        public ConfigNode VesselNode { get; }

        public ConfigNode[] CrewNodes { get; }
    }
}
