using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Time;

namespace MultiKerbal.Tests;

public class WarpPolicyEvaluatorTests
{
    private static readonly WarpContext AtSpaceCenter = new()
    {
        InFlight = false,
        AtSpaceCenter = true,
        StableSituation = true,
        EnginesOff = true,
        RailsMode = true,
        MaxRailsRate = 100_000,
    };

    private static readonly WarpContext Ascending = new()
    {
        InFlight = true,
        StableSituation = false,
        EnginesOff = false,
        InAtmosphere = true,
        RailsMode = true,
        MaxRailsRate = 1,
    };

    private static readonly WarpContext Docking = new()
    {
        InFlight = true,
        StableSituation = true,
        EnginesOff = true,
        VesselsNearby = true,
        RailsMode = true,
        MaxRailsRate = 10_000,
    };

    [Fact]
    public void Defaults_NeitherAcceptNorDeny()
    {
        WarpPolicyDecision decision = WarpPolicyEvaluator.Evaluate(new WarpPolicy(), AtSpaceCenter);

        Assert.False(decision.AutoDeny);
        Assert.Equal(1.0, decision.AcceptUpTo);
    }

    [Fact]
    public void AutoAccept_IsCappedBySettingAndByKsp()
    {
        var policy = new WarpPolicy { AutoAccept = true, AcceptMaxRate = 1000 };

        Assert.Equal(1000, WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter).AcceptUpTo);
        Assert.Equal(50, WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter with { MaxRailsRate = 50 }).AcceptUpTo);
    }

    [Fact]
    public void AutoAccept_RequiresEveryCheckedCondition()
    {
        var policy = new WarpPolicy { AutoAccept = true, AcceptMaxRate = 1000 };

        Assert.Equal(1.0, WarpPolicyEvaluator.Evaluate(policy, Ascending).AcceptUpTo);
        WarpPolicyDecision docking = WarpPolicyEvaluator.Evaluate(policy, Docking);
        Assert.Equal(1.0, docking.AcceptUpTo);
        Assert.Contains("naves cerca", docking.Reason);

        policy.AcceptOnlyAlone = false;
        Assert.Equal(1000, WarpPolicyEvaluator.Evaluate(policy, Docking).AcceptUpTo);
    }

    [Fact]
    public void AutoAccept_OnlyOutsideFlight()
    {
        var policy = new WarpPolicy { AutoAccept = true, AcceptOnlyOutsideFlight = true, AcceptOnlyAlone = false };

        Assert.Equal(1.0, WarpPolicyEvaluator.Evaluate(policy, Docking).AcceptUpTo);
        Assert.Equal(1000, WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter).AcceptUpTo);
    }

    [Fact]
    public void AutoDeny_WithoutConditions_AlwaysWins()
    {
        var policy = new WarpPolicy
        {
            AutoAccept = true,
            AutoDeny = true,
            DenyWhileFlying = false,
            DenyInAtmosphere = false,
            DenyNearVessels = false,
        };

        WarpPolicyDecision decision = WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter);

        Assert.True(decision.AutoDeny);
        Assert.Equal(1.0, decision.AcceptUpTo);
    }

    [Fact]
    public void AutoDeny_TriggersOnAnyCheckedCondition()
    {
        var policy = new WarpPolicy { AutoAccept = true, AutoDeny = true, DenyInAtmosphere = true, DenyNearVessels = false };

        Assert.True(WarpPolicyEvaluator.Evaluate(policy, Ascending).AutoDeny);

        // Sin motivo para rechazar decide el jugador: con el rechazo activado nunca se acepta solo.
        WarpPolicyDecision calm = WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter);
        Assert.False(calm.AutoDeny);
        Assert.Equal(1.0, calm.AcceptUpTo);
    }

    [Fact]
    public void AutoDeny_BlocksAutoAccept_EvenWhenItsConditionsDoNotApply()
    {
        var accepting = new WarpPolicy { AutoAccept = true, AcceptMaxRate = 1000 };
        var denying = new WarpPolicy { AutoAccept = true, AcceptMaxRate = 1000, AutoDeny = true };

        Assert.Equal(1000, WarpPolicyEvaluator.Evaluate(accepting, AtSpaceCenter).AcceptUpTo);
        Assert.Equal(1.0, WarpPolicyEvaluator.Evaluate(denying, AtSpaceCenter).AcceptUpTo);
    }

    [Fact]
    public void PhysicsWarp_IsNeverAutoAccepted()
    {
        var policy = new WarpPolicy { AutoAccept = true };

        Assert.Equal(1.0, WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter with { RailsMode = false }).AcceptUpTo);
    }

    [Fact]
    public void AcceptWhenIdle_IgnoresTheOtherConditions()
    {
        var policy = new WarpPolicy { AcceptWhenIdle = true, IdleSeconds = 60, AcceptMaxRate = 1000 };

        // Con naves cerca y sin aceptación normal activada, pero ausente: acepta igual.
        WarpPolicyDecision idle = WarpPolicyEvaluator.Evaluate(policy, Docking with { IdleSeconds = 120 });

        Assert.Equal(1000, idle.AcceptUpTo);
        Assert.Contains("sin tocar nada", idle.Reason);
    }

    [Fact]
    public void AcceptWhenIdle_NeedsEnoughTimeWithoutInput()
    {
        var policy = new WarpPolicy { AcceptWhenIdle = true, IdleSeconds = 60 };

        WarpPolicyDecision active = WarpPolicyEvaluator.Evaluate(policy, Docking with { IdleSeconds = 10 });

        Assert.Equal(1.0, active.AcceptUpTo);
        Assert.Contains("sin tocar nada", active.Reason);
    }

    [Fact]
    public void AutoDeny_BeatsIdleAcceptance()
    {
        var policy = new WarpPolicy { AcceptWhenIdle = true, IdleSeconds = 1, AutoDeny = true, DenyAtSpaceCenter = true, DenyInAtmosphere = false, DenyNearVessels = false };

        WarpPolicyDecision decision = WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter with { IdleSeconds = 999 });

        Assert.True(decision.AutoDeny);
        Assert.Equal(1.0, decision.AcceptUpTo);
    }

    [Fact]
    public void DenyAtSpaceCenter_OnlyAppliesThere()
    {
        var policy = new WarpPolicy { AutoDeny = true, DenyAtSpaceCenter = true, DenyInAtmosphere = false, DenyNearVessels = false };

        WarpPolicyDecision atCenter = WarpPolicyEvaluator.Evaluate(policy, AtSpaceCenter);
        Assert.True(atCenter.AutoDeny);
        Assert.Contains("Centro Espacial", atCenter.Reason);

        Assert.False(WarpPolicyEvaluator.Evaluate(policy, Docking).AutoDeny);
    }
}

public class WarpConsensusAcceptanceTests
{
    private static WarpVote Vote(string name, double rate, double acceptUpTo = 1, bool deny = false, WarpMode mode = WarpMode.Rails) =>
        new() { PlayerName = name, Participating = true, Rate = rate, Mode = mode, AcceptUpTo = acceptUpTo, AutoDeny = deny };

    [Fact]
    public void AcceptingPlayer_DoesNotLimit()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1000), Vote("Bill", 1, acceptUpTo: 100_000)]);

        Assert.Equal(1000, result.Rate);
        Assert.Equal(string.Empty, result.LimitedBy);
    }

    [Fact]
    public void AcceptingPlayer_LimitsAtOwnMaximum()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1000), Vote("Bill", 1, acceptUpTo: 50)]);

        Assert.Equal(50, result.Rate);
        Assert.Equal("Bill", result.LimitedBy);
        Assert.False(result.LimiterAutoDenies);
    }

    [Fact]
    public void AcceptanceAlone_NeverStartsWarp()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1, acceptUpTo: 1000), Vote("Bill", 1, acceptUpTo: 1000)]);

        Assert.Equal(1.0, result.Rate);
        Assert.Equal(string.Empty, result.LimitedBy);
    }

    [Fact]
    public void AutoDeny_IsReported()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1000), Vote("Bill", 1, acceptUpTo: 100_000, deny: true)]);

        Assert.Equal(1.0, result.Rate);
        Assert.Equal("Bill", result.LimitedBy);
        Assert.True(result.LimiterAutoDenies);
    }

    [Fact]
    public void PhysicsWarp_IsNeverAutoAccepted()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 4, mode: WarpMode.Physics), Vote("Bill", 1, acceptUpTo: 100_000)]);

        Assert.Equal(1.0, result.Rate);
        Assert.Equal("Bill", result.LimitedBy);
    }
}

public class WarpDecisionAcceptanceTests
{
    private static readonly double[] Rails = [1, 5, 10, 50, 100, 1000, 10000, 100000];
    private static readonly double[] Physics = [1, 2, 3, 4];

    [Fact]
    public void Accepting_FollowsGrantAboveOwnDesire()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, 100_000, out _);

        int apply = decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, 100_000, out bool request);

        Assert.Equal(5, apply);
        Assert.False(request);
        Assert.False(decision.Clamped);
        Assert.True(decision.AcceptingOthers);
        Assert.Equal(1.0, decision.DesiredRate(Rails));
    }

    [Fact]
    public void LoweringAcceptedWarp_CapsWhatIsAccepted_UntilReset()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, 100_000, out _);
        decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, 100_000, out _); // aplica x1000

        // KSP (o el jugador) baja a x50.
        int apply = decision.Update(WarpMode.Rails, 3, Rails, 1000, WarpMode.Rails, 100_000, out bool request);

        Assert.Equal(-1, apply);
        Assert.False(request);
        Assert.Equal(50, decision.EffectiveAcceptUpTo(Rails, 100_000));

        decision.ResetAcceptRefusal();
        Assert.Equal(100_000, decision.EffectiveAcceptUpTo(Rails, 100_000));
    }

    [Fact]
    public void RaisingDuringAcceptedWarp_BuildsOnWhatIsApplied()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, 100_000, out _);
        decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, 100_000, out _); // aplica x1000 (índice 5)

        decision.Update(WarpMode.Rails, 6, Rails, 1000, WarpMode.Rails, 100_000, out bool request);

        Assert.True(request);
        Assert.Equal(10_000, decision.DesiredRate(Rails));
    }

    [Fact]
    public void Acceptance_NeedsRailsMode()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Physics, 0, Physics, 1, WarpMode.Rails, 100_000, out _);

        Assert.Equal(-1, decision.Update(WarpMode.Physics, 0, Physics, 1000, WarpMode.Rails, 100_000, out _));
        Assert.False(decision.AcceptingOthers);
    }
}

public sealed class WarpPolicyServerTests : IDisposable
{
    private readonly TestServer _server = new();

    [Fact]
    public void AutoAccept_LetsOthersWarp_AndAutoDenyIsReported()
    {
        TestClient jeb = _server.Join("Jeb");
        TestClient bill = _server.Join("Bill");

        bill.Send(new WarpRequestMessage { Participating = true, Rate = 1, AcceptUpTo = 100_000 });
        jeb.Send(new WarpRequestMessage { Participating = true, Rate = 1000 });
        jeb.WaitFor<TimeStateMessage>(m => m.Rate == 1000 && m.LimitedBy == "");

        bill.Send(new WarpRequestMessage { Participating = true, Rate = 1, AcceptUpTo = 50 });
        jeb.WaitFor<TimeStateMessage>(m => m.Rate == 50 && m.LimitedBy == "Bill" && !m.LimiterAutoDenies);

        bill.Send(new WarpRequestMessage { Participating = true, Rate = 1, AutoDeny = true });
        jeb.WaitFor<TimeStateMessage>(m => m.Rate == 1 && m.LimitedBy == "Bill" && m.LimiterAutoDenies);
    }

    public void Dispose() => _server.Dispose();
}
