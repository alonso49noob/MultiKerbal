using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Tests;

public sealed class SharedControlTests : IDisposable
{
    private static readonly Guid VesselId = Guid.Parse("7a2f4d18-55c1-4a3e-9b8d-1f0c2e3a4b5c");

    private readonly TestServer _server = new();

    [Fact]
    public void Handover_AsksThePilot_AndOnlyThePilotCanGive()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        TestClient val = _server.Join("Val");
        int billId = bill.Welcome!.PlayerId;
        jeb.Send(Proto());
        bill.WaitFor<VesselProtoMessage>();

        bill.Send(new VesselHandoverRequestMessage { VesselId = VesselId });
        Assert.Equal(billId, jeb.WaitFor<VesselHandoverAskMessage>().FromPlayerId);

        // Val no la pilota: su cesión se ignora.
        val.Send(new VesselHandoverGrantMessage { VesselId = VesselId, ToPlayerId = billId });
        val.Send(new ChatMessage { Text = "nada" });
        jeb.WaitFor<ChatMessage>(m => m.Text == "nada");
        Assert.Empty(bill.Received<VesselControlMessage>());

        jeb.Send(new VesselHandoverGrantMessage { VesselId = VesselId, ToPlayerId = billId });
        Assert.Equal(billId, bill.WaitFor<VesselControlMessage>().ControllerId);
        val.WaitFor<ChatMessage>(m => m.Text.Contains("le ha dado el control"));
    }

    [Fact]
    public void PrivateVessel_IsNotHandedToSomeoneWhoCannotPilotIt()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        VesselProtoMessage proto = Proto();
        proto.Access = VesselAccess.Private;
        jeb.Send(proto);
        bill.WaitFor<VesselProtoMessage>();

        bill.Send(new VesselHandoverRequestMessage { VesselId = VesselId });

        // En vez de molestar al piloto, se le recuerda a Bill de quién es la nave.
        Assert.Equal("Jeb", bill.WaitFor<VesselOwnerMessage>().OwnerName);
        Assert.Empty(jeb.Received<VesselHandoverAskMessage>());
    }

    [Fact]
    public void Copilot_ForwardsInputsOnlyToThePilot()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        TestClient val = _server.Join("Val");
        int billId = bill.Welcome!.PlayerId;
        jeb.Send(Proto());
        bill.WaitFor<VesselProtoMessage>();

        jeb.Send(new VesselCopilotMessage { VesselId = VesselId, CopilotId = billId, AllowActions = true });
        Assert.Equal(billId, bill.WaitFor<VesselCopilotMessage>().CopilotId);
        val.WaitFor<VesselCopilotMessage>(m => m.CopilotId == billId);

        bill.Send(new VesselInputMessage { VesselId = VesselId, Pitch = 0.5f });
        Assert.Equal(0.5f, jeb.WaitFor<VesselInputMessage>().Pitch);
        bill.Send(new VesselActionMessage { VesselId = VesselId, Action = VesselAction.Gear });
        Assert.Equal(VesselAction.Gear, jeb.WaitFor<VesselActionMessage>().Action);

        // Val no es copiloto: sus mandos no llegan.
        val.Send(new VesselInputMessage { VesselId = VesselId, Pitch = -1f });
        val.Send(new ChatMessage { Text = "fin" });
        jeb.WaitFor<ChatMessage>(m => m.Text == "fin");
        Assert.Empty(jeb.Received<VesselInputMessage>());
    }

    [Fact]
    public void Copilot_WithoutPermission_CannotUseActionGroups()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        jeb.Send(Proto());
        bill.WaitFor<VesselProtoMessage>();
        jeb.Send(new VesselCopilotMessage { VesselId = VesselId, CopilotId = billId, AllowActions = false });
        bill.WaitFor<VesselCopilotMessage>();

        bill.Send(new VesselActionMessage { VesselId = VesselId, Action = VesselAction.Stage });
        bill.Send(new VesselInputMessage { VesselId = VesselId, Roll = 1f });

        Assert.Equal(1f, jeb.WaitFor<VesselInputMessage>().Roll);
        Assert.Empty(jeb.Received<VesselActionMessage>());
    }

    [Fact]
    public void Copilot_FallsWhenTheVesselChangesHands()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        jeb.Send(Proto());
        bill.WaitFor<VesselProtoMessage>();
        jeb.Send(new VesselCopilotMessage { VesselId = VesselId, CopilotId = billId, AllowActions = true });
        bill.WaitFor<VesselCopilotMessage>(m => m.CopilotId == billId);

        jeb.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = false });

        bill.WaitFor<VesselCopilotMessage>(m => m.CopilotId == 0);
    }

    public void Dispose() => _server.Dispose();

    private static VesselProtoMessage Proto() => new()
    {
        VesselId = VesselId,
        VesselName = "Estación Múnica",
        Access = VesselAccess.Shared,
        Data = [1, 2, 3],
    };
}
