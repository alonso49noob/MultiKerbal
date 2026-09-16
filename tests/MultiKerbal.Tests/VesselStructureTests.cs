using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Serialization;
using MultiKerbal.Server.Persistence;

namespace MultiKerbal.Tests;

public sealed class VesselStructureTests : IDisposable
{
    private readonly TestServer _server = new();

    [Fact]
    public void StructureVersion_IsRelayed_AndSentToLateJoiners()
    {
        var id = Guid.NewGuid();
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");

        jeb.Send(new VesselProtoMessage { VesselId = id, VesselName = "Sonda", StructureVersion = 1, Data = [1] });
        Assert.Equal(1, bill.WaitFor<VesselProtoMessage>(m => m.VesselId == id).StructureVersion);

        // Refresco periódico: misma estructura, datos nuevos.
        jeb.Send(new VesselProtoMessage { VesselId = id, VesselName = "Sonda", StructureVersion = 1, Data = [2] });
        VesselProtoMessage refresh = bill.WaitFor<VesselProtoMessage>(m => m.VesselId == id);
        Assert.Equal(1, refresh.StructureVersion);
        Assert.Equal(new byte[] { 2 }, refresh.Data);

        jeb.Send(new VesselProtoMessage { VesselId = id, VesselName = "Sonda", StructureVersion = 2, Data = [3] });
        bill.WaitFor<VesselProtoMessage>(m => m.VesselId == id && m.StructureVersion == 2);

        TestClient val = _server.Join("Val");
        Assert.Equal(2, val.WaitFor<VesselProtoMessage>(m => m.VesselId == id).StructureVersion);
    }

    [Fact]
    public void StoredVessel_RoundTripsStructureVersion()
    {
        var vessel = new StoredVessel { Id = Guid.NewGuid(), Name = "Sonda", StructureVersion = 7, Data = [1, 2] };

        Assert.Equal(7, VesselStore.Deserialize(VesselStore.Serialize(vessel)).StructureVersion);
    }

    [Fact]
    public void VersionOneFiles_StillLoad()
    {
        var id = Guid.NewGuid();
        var writer = new PacketWriter();
        writer.WriteInt32(1);
        writer.WriteGuid(id);
        writer.WriteString("Antigua");
        writer.WriteBytes([9, 8]);
        writer.WriteBool(false);

        StoredVessel vessel = VesselStore.Deserialize(writer.ToArray());

        Assert.Equal(id, vessel.Id);
        Assert.Equal("Antigua", vessel.Name);
        Assert.Equal(0, vessel.StructureVersion);
        Assert.Equal(new byte[] { 9, 8 }, vessel.Data);
    }

    public void Dispose() => _server.Dispose();
}
