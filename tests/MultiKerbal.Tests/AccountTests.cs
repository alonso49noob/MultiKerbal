using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Vessels;
using MultiKerbal.Server;
using MultiKerbal.Server.Systems;

namespace MultiKerbal.Tests;

public sealed class AccountTests : IDisposable
{
    private readonly TestServer _server = new();

    [Fact]
    public void FirstJoin_RegistersTheName_ThenOnlyItsPasswordGetsIn()
    {
        TestClient first = _server.TryJoinWithAccount("Jeb", "cohete");
        Assert.True(first.Welcome!.Accepted, first.Welcome.RejectReason);
        Assert.True(first.Welcome.NewAccount);
        first.Dispose();

        TestClient impostor = _server.TryJoinWithAccount("jeb", "otra");
        Assert.False(impostor.Welcome!.Accepted);
        Assert.Contains("contraseña", impostor.Welcome.RejectReason);

        TestClient again = _server.TryJoinWithAccount("jeb", "cohete");
        Assert.True(again.Welcome!.Accepted, again.Welcome.RejectReason);
        Assert.False(again.Welcome.NewAccount);
    }

    [Fact]
    public void RegisteredName_KeepsItsSpelling()
    {
        _server.TryJoinWithAccount("Valentina", "cohete").Dispose();

        TestClient val = _server.TryJoinWithAccount("VALENTINA", "cohete");
        TestClient bill = _server.Join("Bill");

        Assert.Contains(bill.WaitFor<PlayerListMessage>().Players, p => p.Name == "Valentina");
        Assert.True(val.Welcome!.Accepted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void NewAccount_NeedsAPasswordLongEnough(string? password)
    {
        TestClient client = _server.TryJoinWithAccount("Bob", password);

        Assert.False(client.Welcome!.Accepted);
    }

    [Fact]
    public void Accounts_SurviveRestarts()
    {
        _server.TryJoinWithAccount("Jeb", "cohete").Dispose();
        _server.Restart();

        Assert.False(_server.TryJoinWithAccount("Jeb", "otra").Welcome!.Accepted);
        Assert.True(_server.TryJoinWithAccount("Jeb", "cohete").Welcome!.Accepted);
    }

    [Fact]
    public void AdminCanDeleteAnAccount_SoTheNameCanPickANewPassword()
    {
        _server.TryJoinWithAccount("Jeb", "olvidada").Dispose();
        Thread.Sleep(200); // Que el servidor procese la desconexión antes del comando.

        _server.Host.EnqueueCommand("account delete Jeb");

        TestClient back = WaitUntilAccepted("Jeb", "nueva");
        Assert.True(back.Welcome!.NewAccount);
    }

    public void Dispose() => _server.Dispose();

    private TestClient WaitUntilAccepted(string name, string password)
    {
        for (int attempt = 0; ; attempt++)
        {
            TestClient client = _server.TryJoinWithAccount(name, password);
            if (client.Welcome!.Accepted || attempt == 20)
                return client;
            Thread.Sleep(50);
        }
    }
}

public sealed class CrewTests : IDisposable
{
    private static readonly Guid First = Guid.Parse("11111111-2d4b-4f8e-9a1c-6b7d8e9f0a12");
    private static readonly Guid Second = Guid.Parse("22222222-2d4b-4f8e-9a1c-6b7d8e9f0a12");

    private readonly TestServer _server = new();

    [Fact]
    public void Kerbal_InSomeoneElsesVessel_CannotBeLaunched()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(First, "Jebediah Kerman", "Bill Kerman"));
        bill.WaitFor<VesselProtoMessage>(m => m.VesselId == First);

        bill.Send(Proto(Second, "jebediah kerman"));

        VesselRejectMessage reject = bill.WaitFor<VesselRejectMessage>();
        Assert.Equal(Second, reject.VesselId);
        Assert.Contains("Jebediah Kerman", reject.Reason);
        Assert.Contains("Jeb", reject.Reason);
        Assert.Empty(jeb.Received<VesselProtoMessage>());
    }

    [Fact]
    public void SamePlayer_CanMoveKerbalsBetweenTheirVessels()
    {
        // EVA: el kerbal sale a una nave nueva antes de que la otra publique que ya no está a bordo.
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(First, "Jebediah Kerman"));
        bill.WaitFor<VesselProtoMessage>(m => m.VesselId == First);

        jeb.Send(Proto(Second, "Jebediah Kerman"));

        Assert.Equal(["Jebediah Kerman"], bill.WaitFor<VesselProtoMessage>(m => m.VesselId == Second).Crew);
        Assert.Empty(jeb.Received<VesselRejectMessage>());
    }

    [Fact]
    public void Kerbal_IsFreeAgain_WhenTheirVesselIsGone()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.Send(Proto(First, "Valentina Kerman"));
        bill.WaitFor<VesselProtoMessage>(m => m.VesselId == First);
        jeb.Send(new VesselRemoveMessage { VesselId = First, Reason = VesselRemoveReason.Recovered });
        bill.WaitFor<VesselRemoveMessage>();

        bill.Send(Proto(Second, "Valentina Kerman"));

        jeb.WaitFor<VesselProtoMessage>(m => m.VesselId == Second);
    }

    [Fact]
    public void Crew_IsSanitized()
    {
        string[] crew = ServerHost.SanitizeCrew(["Jeb", " jeb ", "", null!, "Bill\u0007"]);

        Assert.Equal(["Jeb", "Bill"], crew);
    }

    public void Dispose() => _server.Dispose();

    private static VesselProtoMessage Proto(Guid id, params string[] crew) => new()
    {
        VesselId = id,
        VesselName = "Nave " + id.ToString("N")[..4],
        Access = VesselAccess.Shared,
        Crew = crew,
        Data = [1],
    };
}

public sealed class ProgressTests : IDisposable
{
    private readonly TestServer _server = new(gameMode: "career");

    [Fact]
    public void Career_StartsWithFunds_AndSendsTheStateOnJoin()
    {
        TestClient jeb = _server.Join("Jeb");

        Assert.Equal(GameMode.Career, jeb.Welcome!.GameMode);
        ProgressStateMessage state = jeb.WaitFor<ProgressStateMessage>();
        Assert.Equal(25_000, state.Funds);
        Assert.Contains(ProgressSystem.StartTech, state.Techs);
    }

    [Fact]
    public void Changes_AreAddedUp_AndEveryoneGetsTheTotal()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");
        jeb.WaitFor<ProgressStateMessage>();
        bill.WaitFor<ProgressStateMessage>();

        // Los dos gastan a la vez: se suman las diferencias, no se pisan.
        jeb.Send(new ProgressChangeMessage { Kind = ProgressKind.Funds, Amount = -1000 });
        bill.Send(new ProgressChangeMessage { Kind = ProgressKind.Funds, Amount = -500 });
        bill.Send(new ProgressChangeMessage { Kind = ProgressKind.Science, Amount = 12.5 });
        bill.Send(new ProgressChangeMessage { Kind = ProgressKind.Tech, Id = "basicRocketry" });

        // Los mensajes de dos jugadores pueden llegar en cualquier orden: se espera al estado con todo aplicado.
        ProgressStateMessage last = jeb.WaitFor<ProgressStateMessage>(m => m.Techs.Contains("basicRocketry") && m.Funds == 23_500);
        Assert.Equal(12.5, last.Science);
    }

    [Fact]
    public void Progress_SurvivesRestarts()
    {
        TestClient jeb = _server.Join("Jeb");
        jeb.WaitFor<ProgressStateMessage>();
        jeb.Send(new ProgressChangeMessage { Kind = ProgressKind.Reputation, Amount = 7 });
        jeb.WaitFor<ProgressStateMessage>(m => m.Reputation == 7);
        jeb.Dispose();

        _server.Restart();

        Assert.Equal(7, _server.Join("Jeb").WaitFor<ProgressStateMessage>().Reputation);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2e9)]
    public void AbsurdChanges_AreIgnored(double amount)
    {
        var progress = new ProgressSystem(Path.GetTempPath(), GameMode.Career, 100);

        Assert.False(progress.Apply(new ProgressChangeMessage { Kind = ProgressKind.Funds, Amount = amount }));
    }

    [Theory]
    [InlineData(GameMode.Science, ProgressKind.Funds, false)]
    [InlineData(GameMode.Science, ProgressKind.Science, true)]
    [InlineData(GameMode.Sandbox, ProgressKind.Tech, false)]
    [InlineData(GameMode.Career, ProgressKind.Reputation, true)]
    public void EachMode_SharesItsOwnThings(GameMode mode, ProgressKind kind, bool applies)
    {
        Assert.Equal(applies, GameModes.Applies(mode, kind));
    }

    [Theory]
    [InlineData("basicRocketry", true)]
    [InlineData("", false)]
    [InlineData("../evil", false)]
    public void TechIds_AreValidated(string id, bool valid)
    {
        Assert.Equal(valid, ProgressSystem.IsValidId(id));
    }

    public void Dispose() => _server.Dispose();
}
