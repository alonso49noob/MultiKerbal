using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Tests;

public sealed class SharedProgressTests : IDisposable
{
    private readonly TestServer _server = new(gameMode: "career");

    [Fact]
    public void ScienceSubjects_KeepTheMostScience_AndReachEveryone()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.WaitFor<ScienceSubjectsMessage>(); // Los de al entrar (ninguno).
        bill.WaitFor<ScienceSubjectsMessage>();

        jeb.Send(Subjects(Subject("crewReport@KerbinSrfLanded", 1.0f)));
        Assert.Equal(1.0f, bill.WaitFor<ScienceSubjectsMessage>().Subjects.Single().Science);

        // Menos ciencia que la conocida: no cambia nada ni se reenvía.
        bill.Send(Subjects(Subject("crewReport@KerbinSrfLanded", 0.5f)));
        bill.Send(Subjects(Subject("crewReport@KerbinSrfLanded", 1.4f)));
        Assert.Equal(1.4f, jeb.WaitFor<ScienceSubjectsMessage>().Subjects.Single().Science);

        TestClient val = _server.Join("Val");
        Assert.Equal(1.4f, val.WaitFor<ScienceSubjectsMessage>().Subjects.Single(s => s.Id == "crewReport@KerbinSrfLanded").Science);
    }

    [Fact]
    public void AcceptedContracts_AreShared_AndTheFirstToFinishDecides()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.WaitFor<ContractsMessage>();
        bill.WaitFor<ContractsMessage>();
        Guid id = Guid.NewGuid();

        jeb.Send(Contract(id, ContractStatus.Active));
        Assert.Equal(ContractStatus.Active, bill.WaitFor<ContractsMessage>().Contracts.Single().Status);

        bill.Send(Contract(id, ContractStatus.Completed));
        Assert.Equal(ContractStatus.Completed, jeb.WaitFor<ContractsMessage>().Contracts.Single().Status);

        // Jeb lo da por fallido después: el servidor le corrige con el que ya terminó.
        jeb.Send(Contract(id, ContractStatus.Failed));
        Assert.Equal(ContractStatus.Completed, jeb.WaitFor<ContractsMessage>().Contracts.Single().Status);
    }

    [Fact]
    public void PartsAndFacilities_OnlyGoUp()
    {
        TestClient jeb = _server.Join("Jeb");
        jeb.WaitFor<ProgressStateMessage>();

        jeb.Send(new ProgressChangeMessage { Kind = ProgressKind.Part, Id = "mk1pod.v2" });
        jeb.Send(new ProgressChangeMessage { Kind = ProgressKind.Facility, Id = "SpaceCenter/LaunchPad", Amount = 0.5 });
        jeb.Send(new ProgressChangeMessage { Kind = ProgressKind.Facility, Id = "SpaceCenter/LaunchPad", Amount = 0.25 });
        jeb.Send(new ProgressChangeMessage { Kind = ProgressKind.Funds, Amount = -1 }); // Para saber cuándo se ha procesado todo.

        ProgressStateMessage state = jeb.WaitFor<ProgressStateMessage>(m => m.Funds == 24_999);
        Assert.Contains("mk1pod.v2", state.Parts);
        Assert.Equal(0.5f, state.Facilities.Single(f => f.Id == "SpaceCenter/LaunchPad").Level);
    }

    [Fact]
    public void Everything_SurvivesRestarts()
    {
        TestClient jeb = _server.Join("Jeb");
        jeb.WaitFor<ProgressStateMessage>();
        Guid id = Guid.NewGuid();
        jeb.Send(Subjects(Subject("evaReport@MunSrfLanded", 2f)));
        jeb.Send(Contract(id, ContractStatus.Active));
        jeb.Send(new KerbalsMessage { Kerbals = [new KerbalInfo { Name = "Bob Kerman", Life = KerbalLife.Dead, Node = "name = Bob Kerman" }] });
        jeb.Send(new ProgressChangeMessage { Kind = ProgressKind.Part, Id = "mk1pod.v2" });
        jeb.WaitFor<ProgressStateMessage>(m => m.Parts.Contains("mk1pod.v2"));
        jeb.Dispose();

        _server.Restart();
        TestClient back = _server.Join("Jeb");

        Assert.Equal(KerbalLife.Dead, back.WaitFor<KerbalsMessage>().Kerbals.Single(k => k.Name == "Bob Kerman").Life);
        Assert.Contains("mk1pod.v2", back.WaitFor<ProgressStateMessage>().Parts);
        Assert.Equal(2f, back.WaitFor<ScienceSubjectsMessage>().Subjects.Single().Science);
        Assert.Equal(id, back.WaitFor<ContractsMessage>().Contracts.Single().Id);
    }

    public void Dispose() => _server.Dispose();

    private static ScienceSubjectInfo Subject(string id, float science) => new()
    {
        Id = id,
        Title = id,
        DataScale = 1,
        SubjectValue = 1,
        ScienceCap = 5,
        Science = science,
        ScientificValue = 1 - (science / 5),
    };

    private static ScienceSubjectsMessage Subjects(params ScienceSubjectInfo[] subjects) => new() { Subjects = subjects };

    private static ContractsMessage Contract(Guid id, ContractStatus status) =>
        new() { Contracts = [new ContractInfo { Id = id, Status = status, Node = $"guid = {id}\nstate = {status}\n" }] };
}

public sealed class SandboxRosterTests : IDisposable
{
    private static readonly Guid VesselId = Guid.Parse("7e1f2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid StageId = Guid.Parse("8f2a3b4c-5d6e-4f7a-9b0c-1d2e3f4a5b6c");

    private readonly TestServer _server = new();

    [Fact]
    public void Deaths_AreShared_EvenInSandbox()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        bill.WaitFor<KerbalsMessage>();

        jeb.Send(new KerbalsMessage { Kerbals = [new KerbalInfo { Name = "Valentina Kerman", Life = KerbalLife.Dead, Node = "name = Valentina Kerman" }] });

        Assert.Equal(KerbalLife.Dead, bill.WaitFor<KerbalsMessage>().Kerbals.Single().Life);
        Assert.Empty(jeb.Received<ScienceSubjectsMessage>());
    }

    [Fact]
    public void DestroyedBorrowedVessel_BringsItsCrewBackToLife()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, "Bill Kerman");
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        // Bill la estrella: Bill Kerman muere en su partida y luego la nave desaparece.
        bill.Send(new KerbalsMessage { Kerbals = [new KerbalInfo { Name = "Bill Kerman", Life = KerbalLife.Dead, Node = "name = Bill Kerman" }] });
        bill.Send(new VesselRemoveMessage { VesselId = VesselId, Reason = VesselRemoveReason.Destroyed });

        jeb.WaitFor<KerbalsMessage>(m => m.Kerbals.Any(k => k.Life == KerbalLife.Dead));
        Assert.Equal(KerbalLife.Alive, jeb.WaitFor<KerbalsMessage>(m => m.Kerbals.Any(k => k.Life == KerbalLife.Alive)).Kerbals.Single().Life);
        Assert.Equal(KerbalLife.Alive, bill.WaitFor<KerbalsMessage>(m => m.Kerbals.Any(k => k.Life == KerbalLife.Alive)).Kerbals.Single().Life);
    }

    [Fact]
    public void StagesDroppedFromABorrowedVessel_StayTheOwners_ButTheBorrowerCanLoseThem()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill, access: VesselAccess.Private);
        jeb.Send(new VesselLendRequestMessage { VesselId = VesselId, PlayerName = "Bill" });
        bill.WaitFor<VesselOwnerMessage>(m => m.LentTo == "Bill");
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        bill.Send(new VesselProtoMessage { VesselId = StageId, ParentId = VesselId, VesselName = "Etapa", Access = VesselAccess.Public, Data = [2] });

        VesselProtoMessage stage = jeb.WaitFor<VesselProtoMessage>(m => m.VesselId == StageId);
        Assert.Equal("Jeb", stage.OwnerName);
        Assert.Equal(VesselAccess.Private, stage.Access);
        Assert.Equal("Bill", stage.LentTo);

        // Se quema al caer: la perdió quien la soltó, así que desaparece.
        bill.Send(new VesselRemoveMessage { VesselId = StageId, Reason = VesselRemoveReason.Destroyed });
        Assert.Equal(StageId, jeb.WaitFor<VesselRemoveMessage>().VesselId);
    }

    [Fact]
    public void Owner_CanRestoreAVesselReturnedDamaged()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        Assert.True(jeb.WaitFor<VesselOwnerMessage>(m => m.Restorable).Restorable);
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == billId);

        // Bill la devuelve sin una pieza.
        bill.Send(new VesselProtoMessage { VesselId = VesselId, VesselName = "Estación", Data = [9, 9] });
        jeb.WaitFor<VesselProtoMessage>(m => m.Data.Length == 2);

        // Mientras Bill la pilota, no se puede restaurar.
        jeb.Send(new VesselRestoreRequestMessage { VesselId = VesselId });
        jeb.WaitFor<VesselOwnerMessage>(m => m.Restorable);

        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = false });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);
        jeb.Send(new VesselRestoreRequestMessage { VesselId = VesselId });

        VesselProtoMessage restored = bill.WaitFor<VesselProtoMessage>(m => m.Data.Length == 1);
        Assert.False(restored.Restorable);
    }

    [Fact]
    public void OwnerTakingTheVessel_AcceptsItAsItIs()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        int billId = bill.Welcome!.PlayerId;
        PublishAndRelease(jeb, bill);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });
        jeb.WaitFor<VesselOwnerMessage>(m => m.Restorable);
        bill.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = false });
        jeb.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);

        jeb.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = true });

        Assert.False(bill.WaitFor<VesselOwnerMessage>(m => !m.Restorable).Restorable);
    }

    public void Dispose() => _server.Dispose();

    private static void PublishAndRelease(TestClient jeb, TestClient other, string? crew = null, VesselAccess access = VesselAccess.Shared)
    {
        jeb.Send(new VesselProtoMessage
        {
            VesselId = VesselId,
            VesselName = "Estación",
            Access = access,
            Crew = crew == null ? [] : [crew],
            Data = [1],
        });
        other.WaitFor<VesselProtoMessage>(m => m.VesselId == VesselId);
        jeb.WaitFor<VesselOwnerMessage>();
        jeb.Send(new VesselControlRequestMessage { VesselId = VesselId, Acquire = false });
        other.WaitFor<VesselControlMessage>(m => m.ControllerId == 0);
    }
}
