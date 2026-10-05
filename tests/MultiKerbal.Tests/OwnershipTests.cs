using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Tests;

public class VesselPermissionsTests
{
    [Theory]
    // dueño, acceso, prestada a, jugador, pilotar, recuperar/borrar, cambiar propiedad
    [InlineData("Jeb", VesselAccess.Private, null, "Jeb", true, true, true)]
    [InlineData("Jeb", VesselAccess.Private, null, "jeb", true, true, true)]
    [InlineData("Jeb", VesselAccess.Private, null, "Bill", false, false, false)]
    [InlineData("Jeb", VesselAccess.Shared, null, "Bill", true, false, false)]
    [InlineData("Jeb", VesselAccess.Public, null, "Bill", true, true, false)]
    [InlineData("", VesselAccess.Private, null, "Bill", true, true, true)]
    [InlineData(null, VesselAccess.Shared, null, "Bill", true, true, true)]
    // Prestada: quien la tiene puede pilotarla aunque sea privada, pero no destruirla ni regalarla.
    [InlineData("Jeb", VesselAccess.Private, "Bill", "bill", true, false, false)]
    [InlineData("Jeb", VesselAccess.Private, "Bill", "Val", false, false, false)]
    public void Rules(string? owner, VesselAccess access, string? lentTo, string player, bool pilot, bool remove, bool change)
    {
        Assert.Equal(pilot, VesselPermissions.CanPilot(owner!, access, lentTo!, player));
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
    public void DestroyingSomeoneElsesVessel_BringsItBackWhereItWasTaken()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, VesselAccess.Shared);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        // Bill la daña (pierde piezas) y luego la estrella.
        bill.Send(Proto(VesselAccess.Shared, 6, 6));
        jeb.WaitFor<VesselProtoMessage>(m => m.Data.Length == 2);
        bill.Send(new VesselRemoveMessage { VesselId = VesselId, Reason = VesselRemoveReason.Destroyed });

        // No desaparece: vuelve a como estaba cuando Bill la tomó, sin piloto, y todos la recargan.
        VesselProtoMessage restored = jeb.WaitFor<VesselProtoMessage>(m => m.Data.Length == 1);
        Assert.Equal(0, restored.ControllerId);
        Assert.Equal(new byte[] { 1 }, bill.WaitFor<VesselProtoMessage>(m => m.Data.Length == 1).Data);
        Assert.Contains("Jeb", bill.WaitFor<ChatMessage>(m => m.SenderId == ChatMessage.SystemSenderId && m.Text.Contains("Estación")).Text);
        Assert.Empty(jeb.Received<VesselRemoveMessage>());
    }

    [Fact]
    public void Pilot_CannotRecoverSomeoneElsesVessel_ButOwnerCanDestroyTheirs()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, VesselAccess.Shared);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        // Recuperarla no vale: se queda donde estaba (posada), sin piloto.
        bill.Send(new VesselRemoveMessage { VesselId = VesselId, Reason = VesselRemoveReason.Recovered });
        Assert.Equal(0, bill.WaitFor<VesselProtoMessage>().ControllerId);
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);

        jeb.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        bill.WaitFor<VesselControlMessage>(m => m.ControllerId == jeb.Welcome!.PlayerId);
        jeb.Send(new VesselRemoveMessage { VesselId = VesselId, Reason = VesselRemoveReason.Destroyed });
        Assert.Equal(VesselRemoveReason.Destroyed, bill.WaitFor<VesselRemoveMessage>().Reason);
    }

    [Fact]
    public void Pilot_CanMergeSomeoneElsesVessel_ByDocking()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, VesselAccess.Shared);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        bill.Send(new VesselRemoveMessage { VesselId = VesselId, Reason = VesselRemoveReason.Merged });
        Assert.Equal(VesselRemoveReason.Merged, jeb.WaitFor<VesselRemoveMessage>().Reason);
    }

    [Fact]
    public void LentVessel_BorrowerFliesItEvenIfPrivate_UntilOwnerTakesItBack()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        TestClient val = _server.Join("Val");
        int billId = bill.Welcome!.PlayerId;
        jeb.Send(Proto(VesselAccess.Private));
        bill.WaitFor<VesselProtoMessage>();
        val.WaitFor<VesselProtoMessage>();
        jeb.WaitFor<VesselOwnerMessage>();
        jeb.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = false });
        bill.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);

        // Solo el dueño presta.
        val.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = "Val" });
        Assert.Equal(string.Empty, val.WaitFor<VesselOwnerMessage>().LentTo ?? string.Empty);

        jeb.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = "bill" });
        Assert.Equal("Bill", val.WaitFor<VesselOwnerMessage>(m => !string.IsNullOrEmpty(m.LentTo)).LentTo);

        // Bill puede pilotarla aunque sea privada; Val no.
        val.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        Assert.Equal(0, val.WaitFor<VesselControlMessage>().ControllerId);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        // Jeb la recupera: Bill deja de pilotarla.
        jeb.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = string.Empty });
        bill.WaitFor<VesselOwnerMessage>(m => string.IsNullOrEmpty(m.LentTo));
        bill.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);
    }

    [Fact]
    public void Borrower_CanGiveItBack_ButNotLendItToSomeoneElse()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        _server.Join("Val");
        PublishAndRelease(jeb, bill, VesselAccess.Private);
        jeb.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = "Bill" });
        bill.WaitFor<VesselOwnerMessage>(m => m.LentTo == "Bill");

        bill.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = "Val" });
        Assert.Equal("Bill", bill.WaitFor<VesselOwnerMessage>().LentTo);

        bill.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = string.Empty });
        Assert.True(string.IsNullOrEmpty(jeb.WaitFor<VesselOwnerMessage>(m => string.IsNullOrEmpty(m.LentTo)).LentTo));
    }

    [Fact]
    public void GivingTheVesselAway_EndsTheLoan()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        _server.Join("Val");
        PublishAndRelease(jeb, bill, VesselAccess.Private);
        jeb.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = "Bill" });
        bill.WaitFor<VesselOwnerMessage>(m => m.LentTo == "Bill");

        jeb.Send(new VesselOwnerRequestMessage { VesselId = VesselId, OwnerName = "Val", Access = VesselAccess.Private });
        VesselOwnerMessage gifted = bill.WaitFor<VesselOwnerMessage>(m => m.OwnerName == "Val");
        Assert.True(string.IsNullOrEmpty(gifted.LentTo));
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
