using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Tests;

public class VesselPermissionsTests
{
    [Theory]
    // dueño, acceso, jugador, pilotar, recuperar/borrar, cambiar propiedad
    [InlineData("Jeb", VesselAccess.Private, "Jeb", true, true, true)]
    [InlineData("Jeb", VesselAccess.Private, "jeb", true, true, true)]
    [InlineData("Jeb", VesselAccess.Private, "Bill", false, false, false)]
    [InlineData("Jeb", VesselAccess.Shared, "Bill", true, false, false)]
    [InlineData("Jeb", VesselAccess.Public, "Bill", true, true, false)]
    [InlineData("", VesselAccess.Private, "Bill", true, true, true)]
    [InlineData(null, VesselAccess.Shared, "Bill", true, true, true)]
    public void Rules(string? owner, VesselAccess access, string player, bool pilot, bool remove, bool change)
    {
        Assert.Equal(pilot, VesselPermissions.CanPilot(owner!, access, player));
        Assert.Equal(remove, VesselPermissions.CanRemove(owner!, access, player));
        Assert.Equal(change, VesselPermissions.CanChangeOwnership(owner!, player));
    }

    [Fact]
    public void UnknownAccessValues_AreInvalid()
    {
        Assert.True(VesselPermissions.IsValid(VesselAccess.Public));
        Assert.False(VesselPermissions.IsValid((VesselAccess)7));
    }
}

public sealed class VesselOwnershipTests : IDisposable
{
    private static readonly Guid VesselId = Guid.Parse("3c9e5a71-2d4b-4f8e-9a1c-6b7d8e9f0a12");

    private readonly TestServer _server = new();

    [Fact]
    public void NewVessel_BelongsToPublisher_WithRequestedAccess()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");

        jeb.Send(Proto(VesselAccess.Private));

        VesselProtoMessage relayed = bill.WaitFor<VesselProtoMessage>();
        Assert.Equal("Jeb", relayed.OwnerName);
        Assert.Equal(VesselAccess.Private, relayed.Access);
        VesselOwnerMessage confirmed = jeb.WaitFor<VesselOwnerMessage>();
        Assert.Equal("Jeb", confirmed.OwnerName);
    }

    [Fact]
    public void ClientCannotChangeOwner_ByRepublishing()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(VesselAccess.Shared));
        bill.WaitFor<VesselProtoMessage>();

        VesselProtoMessage forged = Proto(VesselAccess.Public);
        forged.OwnerName = "Bill";
        jeb.Send(forged);

        VesselProtoMessage relayed = bill.WaitFor<VesselProtoMessage>();
        Assert.Equal("Jeb", relayed.OwnerName);
        Assert.Equal(VesselAccess.Shared, relayed.Access);
    }

    [Fact]
    public void PrivateVessel_OnlyOwnerCanPilotIt()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int jebId = jeb.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, VesselAccess.Private);

        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        Assert.Equal(0, bill.WaitFor<VesselControlMessage>().ControllerId);

        // Publicar la nave también es tomar el control: se rechaza y se devuelve la versión buena.
        bill.Send(Proto(VesselAccess.Private, 9));
        Assert.Equal(new byte[] { 1 }, bill.WaitFor<VesselProtoMessage>().Data);

        jeb.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        bill.WaitFor<VesselControlMessage>(m => m.ControllerId == jebId);
    }

    [Fact]
    public void SharedVessel_OthersCanPilotIt_ButOnlyOwnerRemovesIt()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, VesselAccess.Shared);

        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        // Mientras Jeb no la pilota, ni el dueño la borra: la está usando Bill.
        jeb.Send(new VesselRemoveMessage { VesselId = VesselId });
        Assert.Equal(billId, jeb.WaitFor<VesselProtoMessage>().ControllerId);

        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = false });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);

        bill.Send(new VesselRemoveMessage { VesselId = VesselId });
        Assert.Equal("Jeb", bill.WaitFor<VesselProtoMessage>().OwnerName);

        jeb.Send(new VesselRemoveMessage { VesselId = VesselId });
        bill.WaitFor<VesselRemoveMessage>();
    }

    [Fact]
    public void PilotCanLoseASharedVessel()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, VesselAccess.Shared);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        // Choque con Bill a los mandos: la nave desaparece aunque no sea suya.
        bill.Send(new VesselRemoveMessage { VesselId = VesselId });
        jeb.WaitFor<VesselRemoveMessage>();
    }

    [Fact]
    public void OnlyOwner_ChangesAccess_AndGiftsOnlyToConnectedPlayers()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        PublishAndRelease(jeb, bill, VesselAccess.Shared);

        bill.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Bill", Access = VesselAccess.Public });
        Assert.Equal("Jeb", bill.WaitFor<VesselOwnerMessage>().OwnerName);

        jeb.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Valentina", Access = VesselAccess.Shared });
        Assert.Equal("Jeb", jeb.WaitFor<VesselOwnerMessage>().OwnerName);

        jeb.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Jeb", Access = VesselAccess.Public });
        Assert.Equal(VesselAccess.Public, bill.WaitFor<VesselOwnerMessage>().Access);
        jeb.WaitFor<VesselOwnerMessage>(m => m.Access == VesselAccess.Public);

        jeb.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "bill", Access = VesselAccess.Private });
        VesselOwnerMessage gifted = bill.WaitFor<VesselOwnerMessage>();
        Assert.Equal("Bill", gifted.OwnerName);
        Assert.Equal(VesselAccess.Private, gifted.Access);
        bill.WaitFor<ChatMessage>(m => m.Text.Contains("regalado"));

        // Ya no es de Jeb.
        jeb.WaitFor<VesselOwnerMessage>(m => m.OwnerName == "Bill");
        jeb.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Jeb", Access = VesselAccess.Public });
        Assert.Equal("Bill", jeb.WaitFor<VesselOwnerMessage>().OwnerName);
    }

    [Fact]
    public void MakingItPrivate_RemovesOtherPilot()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, VesselAccess.Shared);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        jeb.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Jeb", Access = VesselAccess.Private });

        bill.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);
    }

    [Fact]
    public void AbandonedVessel_CanBeClaimed_OnlyForYourself()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        TestClient val = _server.Join("Val");
        PublishAndRelease(jeb, bill, VesselAccess.Private);
        val.WaitFor<VesselProtoMessage>();

        jeb.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = string.Empty, Access = VesselAccess.Private });
        Assert.Equal(string.Empty, bill.WaitFor<VesselOwnerMessage>().OwnerName);

        bill.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Val", Access = VesselAccess.Shared });
        Assert.Equal(string.Empty, bill.WaitFor<VesselOwnerMessage>().OwnerName);

        bill.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Bill", Access = VesselAccess.Shared });
        Assert.Equal("Bill", val.WaitFor<VesselOwnerMessage>(m => m.OwnerName.Length > 0).OwnerName);
    }

    public void Dispose() => _server.Dispose();

    private static VesselProtoMessage Proto(VesselAccess access, params byte[] data) => new()
    {
        VesselId = VesselId,
        VesselName = "Estación",
        Access = access,
        Data = data.Length > 0 ? data : [1],
    };

    /// <summary>Jeb la publica y la suelta (como al volver al Centro Espacial).</summary>
    private static void PublishAndRelease(TestClient jeb, TestClient other, VesselAccess access)
    {
        jeb.Send(Proto(access));
        other.WaitFor<VesselProtoMessage>();
        jeb.WaitFor<VesselOwnerMessage>();
        jeb.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = false });
        other.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);
    }
}
