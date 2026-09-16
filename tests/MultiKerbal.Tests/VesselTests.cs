using System.Text;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Serialization;
using MultiKerbal.Server;
using MultiKerbal.Server.Persistence;

namespace MultiKerbal.Tests;

public sealed class VesselSyncTests : IDisposable
{
    private static readonly Guid VesselId = Guid.Parse("0b7d2c1e-8f4a-4d2b-a6c3-5e9f1d7a3b22");

    private readonly TestServer _server = new();

    [Fact]
    public void Proto_IsRelayedWithOwner_AndSentToLateJoiners()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int jebId = jeb.Welcome!.PlayerId;

        jeb.Send(Proto(1, 2, 3));

        VesselProtoMessage relayed = bill.WaitFor<VesselProtoMessage>(m => m.VesselId == VesselId);
        Assert.Equal(jebId, relayed.OwnerId);
        Assert.Equal("Kerbal X", relayed.VesselName);
        Assert.Equal(new byte[] { 1, 2, 3 }, relayed.Data);
        Assert.Equal(jebId, jeb.WaitFor<VesselOwnershipMessage>(m => m.VesselId == VesselId).OwnerId);

        TestClient val = _server.Join("Val");
        Assert.Equal(jebId, val.WaitFor<VesselProtoMessage>(m => m.VesselId == VesselId).OwnerId);
    }

    [Fact]
    public void Updates_OnlyFromOwner_AreRelayedAndKept()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(1));
        bill.WaitFor<VesselProtoMessage>();

        jeb.Send(Update(10));
        Assert.Equal(10, bill.WaitFor<VesselUpdateMessage>().UniversalTime);

        bill.Send(Update(999)); // Bill no es el dueño: se ignora.
        bill.Send(new ChatMessage { Text = "sincronizar" });
        jeb.WaitFor<ChatMessage>(m => m.Text == "sincronizar");
        Assert.Empty(jeb.Received<VesselUpdateMessage>());

        TestClient val = _server.Join("Val");
        Assert.Equal(10, val.WaitFor<VesselUpdateMessage>().UniversalTime);
    }

    [Fact]
    public void InvalidUpdates_AreDropped()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(1));
        bill.WaitFor<VesselProtoMessage>();

        VesselUpdateMessage broken = Update(5);
        broken.SemiMajorAxis = double.NaN;
        jeb.Send(broken);
        jeb.Send(new ChatMessage { Text = "listo" });
        bill.WaitFor<ChatMessage>(m => m.Text == "listo");

        Assert.Empty(bill.Received<VesselUpdateMessage>());
    }

    [Fact]
    public void NonOwner_CannotOverwriteOrRemoveVessel()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(1));
        bill.WaitFor<VesselProtoMessage>();

        bill.Send(Proto(9, 9));
        Assert.Equal(new byte[] { 1 }, bill.WaitFor<VesselProtoMessage>().Data);

        bill.Send(new VesselRemoveMessage { VesselId = VesselId });
        Assert.Equal(jeb.Welcome!.PlayerId, bill.WaitFor<VesselProtoMessage>().OwnerId);
    }

    [Fact]
    public void Remove_ByOwner_IsRelayedAndForgotten()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(1));
        bill.WaitFor<VesselProtoMessage>();

        jeb.Send(new VesselRemoveMessage { VesselId = VesselId });
        bill.WaitFor<VesselRemoveMessage>(m => m.VesselId == VesselId);

        TestClient val = _server.Join("Val");
        // El aviso de entrada se envía después de las naves: si hubiera alguna, ya habría llegado.
        val.WaitFor<ChatMessage>(m => m.Text.Contains("Val"));
        Assert.Empty(val.Received<VesselProtoMessage>());
    }

    [Fact]
    public void Ownership_IsReleasedOnDisconnect_ThenFirstComeFirstServed()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        TestClient val = _server.Join("Val");
        int billId = bill.Welcome!.PlayerId;
        jeb.Send(Proto(1));
        bill.WaitFor<VesselProtoMessage>();
        val.WaitFor<VesselProtoMessage>();

        jeb.Net.Disconnect("Adiós");
        bill.WaitFor<VesselOwnershipMessage>(m => m.OwnerId == 0);

        bill.Send(new VesselOwnershipRequestMessage { VesselId = VesselId, Acquire = true });
        bill.WaitFor<VesselOwnershipMessage>(m => m.OwnerId == billId);
        val.WaitFor<VesselOwnershipMessage>(m => m.OwnerId == billId);

        // Val llega tarde: la petición se deniega y se le informa de quién la controla.
        val.Send(new VesselOwnershipRequestMessage { VesselId = VesselId, Acquire = true });
        val.WaitFor<VesselOwnershipMessage>(m => m.OwnerId == billId);

        bill.Send(new VesselOwnershipRequestMessage { VesselId = VesselId, Acquire = false });
        val.WaitFor<VesselOwnershipMessage>(m => m.OwnerId == 0);
    }

    [Fact]
    public void Vessels_PersistAcrossRestarts_WithoutOwner()
    {
        TestClient jeb = _server.Join("Jeb");
        jeb.Send(Proto(4, 5, 6));
        jeb.WaitFor<VesselOwnershipMessage>();
        jeb.Send(Update(42));
        jeb.Send(new ChatMessage { Text = "fin" });
        jeb.WaitFor<ChatMessage>(m => m.Text == "fin");

        _server.Restart();

        TestClient bill = _server.Join("Bill");
        VesselProtoMessage proto = bill.WaitFor<VesselProtoMessage>();
        Assert.Equal(new byte[] { 4, 5, 6 }, proto.Data);
        Assert.Equal(0, proto.OwnerId);
        Assert.Equal(42, bill.WaitFor<VesselUpdateMessage>().UniversalTime);
    }

    public void Dispose() => _server.Dispose();

    private static VesselProtoMessage Proto(params byte[] data) =>
        new() { VesselId = VesselId, VesselName = "Kerbal X", Data = data };

    private static VesselUpdateMessage Update(double universalTime) => new()
    {
        VesselId = VesselId,
        UniversalTime = universalTime,
        BodyIndex = 1,
        Situation = 32,
        SemiMajorAxis = 700_000,
        Epoch = universalTime,
        RotationW = 1f,
    };
}

public sealed class VesselStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MultiKerbalTests", Guid.NewGuid().ToString("N"));

    public VesselStoreTests()
    {
        Log.ConsoleEnabled = false;
    }

    [Fact]
    public void Serialize_RoundTrips_WithAndWithoutUpdate()
    {
        var plain = new StoredVessel { Id = Guid.NewGuid(), Name = "Sonda", Data = [7, 8] };
        var withUpdate = new StoredVessel
        {
            Id = Guid.NewGuid(),
            Name = "Estación Múnica",
            Data = [1],
            LastUpdate = new VesselUpdateMessage { UniversalTime = 99, SemiMajorAxis = 250_000 },
        };

        StoredVessel plainCopy = VesselStore.Deserialize(VesselStore.Serialize(plain));
        StoredVessel updateCopy = VesselStore.Deserialize(VesselStore.Serialize(withUpdate));

        Assert.Equal(plain.Id, plainCopy.Id);
        Assert.Equal(new byte[] { 7, 8 }, plainCopy.Data);
        Assert.Null(plainCopy.LastUpdate);
        Assert.Equal("Estación Múnica", updateCopy.Name);
        Assert.Equal(250_000, updateCopy.LastUpdate!.SemiMajorAxis);
    }

    [Fact]
    public void SaveAndLoad_KeepsVessels_ForgetsRemovedOnes_AndDropsOwners()
    {
        Guid kept = Guid.NewGuid();
        Guid removed = Guid.NewGuid();
        var store = new VesselStore(_directory);
        StoredVessel vessel = store.GetOrCreate(kept);
        vessel.Name = "Kept";
        vessel.Data = [1];
        vessel.OwnerId = 3;
        store.GetOrCreate(removed).Data = [2];
        store.SaveDirty();
        store.Remove(removed);
        store.SaveDirty();

        var reloaded = new VesselStore(_directory);
        reloaded.Load();

        StoredVessel loaded = Assert.Single(reloaded.All);
        Assert.Equal(kept, loaded.Id);
        Assert.Equal(0, loaded.OwnerId);
        Assert.Single(Directory.GetFiles(reloaded.DirectoryPath));
    }

    [Fact]
    public void CorruptFiles_AreSkipped()
    {
        var store = new VesselStore(_directory);
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "roto.vessel"), [1, 2, 3]);

        store.Load();

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void UpdateValidation_RejectsNonFiniteValues()
    {
        var valid = new VesselUpdateMessage { VesselId = Guid.NewGuid(), UniversalTime = 1, RotationW = 1 };
        var nan = new VesselUpdateMessage { VesselId = Guid.NewGuid(), UniversalTime = 1, Eccentricity = double.NaN };
        var infinite = new VesselUpdateMessage { VesselId = Guid.NewGuid(), UniversalTime = 1, RotationX = float.PositiveInfinity };

        Assert.True(ServerHost.IsValid(valid));
        Assert.False(ServerHost.IsValid(nan));
        Assert.False(ServerHost.IsValid(infinite));
        Assert.False(ServerHost.IsValid(new VesselUpdateMessage()));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class CompressionTests
{
    [Fact]
    public void RoundTrip_ShrinksRepetitiveConfigText()
    {
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("PART\n{\n\tname = fuelTank\n}\n", 500)));

        byte[] compressed = Compression.Compress(data);

        Assert.True(compressed.Length < data.Length / 10);
        Assert.Equal(data, Compression.Decompress(compressed));
    }

    [Fact]
    public void CorruptData_Throws()
    {
        Assert.Throws<ProtocolException>(() => Compression.Decompress([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
    }

    [Fact]
    public void OutputBeyondLimit_Throws()
    {
        byte[] compressed = Compression.Compress(new byte[10_000]);

        Assert.Throws<ProtocolException>(() => Compression.Decompress(compressed, maxBytes: 1000));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, false)]
    [InlineData(32, false)]
    public void SurfaceSituations_MatchKsp(int situation, bool onSurface)
    {
        Assert.Equal(onSurface, new VesselUpdateMessage { Situation = situation }.IsOnSurface);
    }
}
