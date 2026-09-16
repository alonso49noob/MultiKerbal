using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Time;

namespace MultiKerbal.Tests;

public class WarpConsensusTests
{
    private static WarpVote Vote(string name, double rate, WarpMode mode = WarpMode.Rails, bool participating = true) =>
        new() { PlayerName = name, Rate = rate, Mode = mode, Participating = participating };

    [Fact]
    public void NoParticipants_RunsAtNormalSpeed()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1000, participating: false)]);

        Assert.Equal(1.0, result.Rate);
        Assert.Equal(string.Empty, result.LimitedBy);
    }

    [Fact]
    public void SlowestParticipant_LimitsEveryone()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1000), Vote("Bill", 50), Vote("Val", 100000)]);

        Assert.Equal(50, result.Rate);
        Assert.Equal(WarpMode.Rails, result.Mode);
        Assert.Equal("Bill", result.LimitedBy);
    }

    [Fact]
    public void NonParticipants_DoNotLimit()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1000), Vote("Bill", 1, participating: false)]);

        Assert.Equal(1000, result.Rate);
        Assert.Equal(string.Empty, result.LimitedBy);
    }

    [Fact]
    public void Agreement_ReportsNoLimiter()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 10), Vote("Bill", 10)]);

        Assert.Equal(10, result.Rate);
        Assert.Equal(string.Empty, result.LimitedBy);
    }

    [Fact]
    public void PhysicsWarp_IsAgreedWhenEveryoneUsesIt()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 4, WarpMode.Physics), Vote("Bill", 2, WarpMode.Physics)]);

        Assert.Equal(2, result.Rate);
        Assert.Equal(WarpMode.Physics, result.Mode);
        Assert.Equal("Bill", result.LimitedBy);
    }

    [Fact]
    public void MixedModes_FallBackToNormalSpeed()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1000), Vote("Bill", 4, WarpMode.Physics)]);

        Assert.Equal(1.0, result.Rate);
        Assert.Equal(TimeStateMessage.MixedModes, result.LimitedBy);
    }

    [Fact]
    public void PlayerAtNormalSpeed_IsCompatibleWithEitherMode()
    {
        WarpConsensusResult result = WarpConsensus.Compute([Vote("Jeb", 1, WarpMode.Rails), Vote("Bill", 4, WarpMode.Physics)]);

        Assert.Equal(1.0, result.Rate);
        Assert.Equal("Jeb", result.LimitedBy);
    }

    [Fact]
    public void InvalidRates_AreNormalized()
    {
        Assert.Equal(1.0, WarpConsensus.Compute([Vote("Jeb", double.NaN)]).Rate);
        Assert.Equal(1.0, WarpConsensus.Compute([Vote("Jeb", 0.1)]).Rate);
        Assert.Equal(1e7, WarpConsensus.Compute([Vote("Jeb", double.PositiveInfinity)]).Rate);
    }
}

public class WarpDecisionTests
{
    private static readonly double[] Rails = [1, 5, 10, 50, 100, 1000, 10000, 100000];
    private static readonly double[] Physics = [1, 2, 3, 4];

    [Fact]
    public void FirstUpdate_RequestsCurrentState()
    {
        var decision = new WarpDecision();

        int apply = decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, out bool request);

        Assert.Equal(-1, apply);
        Assert.True(request);
        Assert.Equal(1.0, decision.DesiredRate(Rails));
    }

    [Fact]
    public void RaisingWithinGrant_IsKept()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, out _);

        int apply = decision.Update(WarpMode.Rails, 3, Rails, 1000, WarpMode.Rails, out bool request);

        Assert.Equal(-1, apply);
        Assert.True(request);
        Assert.False(decision.Clamped);
        Assert.Equal(50, decision.DesiredRate(Rails));
    }

    [Fact]
    public void RaisingBeyondGrant_IsRevertedButRequested()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, out _);

        int apply = decision.Update(WarpMode.Rails, 1, Rails, 1, WarpMode.Rails, out bool request);

        Assert.Equal(0, apply);
        Assert.True(request);
        Assert.True(decision.Clamped);
        Assert.Equal(5, decision.DesiredRate(Rails));
    }

    [Fact]
    public void RepeatedRaisesWhileClamped_AccumulateDesire()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, out _);
        decision.Update(WarpMode.Rails, 1, Rails, 1, WarpMode.Rails, out _); // revertido a 0

        // KSP vuelve a subir desde el índice aplicado (0) hasta 1.
        decision.Update(WarpMode.Rails, 1, Rails, 1, WarpMode.Rails, out bool request);

        Assert.True(request);
        Assert.Equal(10, decision.DesiredRate(Rails));
    }

    [Fact]
    public void GrantCatchingUp_AppliesDesiredWarp()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, out _);
        decision.Update(WarpMode.Rails, 5, Rails, 1, WarpMode.Rails, out _); // quiere x1000, se queda en x1

        int apply = decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, out bool request);

        Assert.Equal(5, apply);
        Assert.False(request);
        Assert.False(decision.Clamped);
    }

    [Fact]
    public void PartialGrant_AppliesLargestAllowedStep()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, out _);
        decision.Update(WarpMode.Rails, 5, Rails, 1, WarpMode.Rails, out _);

        int apply = decision.Update(WarpMode.Rails, 0, Rails, 50, WarpMode.Rails, out _);

        Assert.Equal(3, apply);
        Assert.True(decision.Clamped);
    }

    [Fact]
    public void LoweringByKsp_LowersDesire()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, out _);
        decision.Update(WarpMode.Rails, 5, Rails, 1000, WarpMode.Rails, out _);

        int apply = decision.Update(WarpMode.Rails, 2, Rails, 1000, WarpMode.Rails, out bool request);

        Assert.Equal(-1, apply);
        Assert.True(request);
        Assert.Equal(10, decision.DesiredRate(Rails));
    }

    [Fact]
    public void SwitchingToPhysicsWarp_WaitsForPhysicsGrant()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, out _);

        int apply = decision.Update(WarpMode.Physics, 2, Physics, 1000, WarpMode.Rails, out bool request);

        Assert.Equal(0, apply);
        Assert.True(request);
        Assert.True(decision.Clamped);
        Assert.Equal(WarpMode.Physics, decision.DesiredMode);
        Assert.Equal(3, decision.DesiredRate(Physics));
    }

    [Fact]
    public void CancelDesire_DropsRequestToAppliedWarp()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Rails, 0, Rails, 1, WarpMode.Rails, out _);
        decision.Update(WarpMode.Rails, 5, Rails, 1, WarpMode.Rails, out _); // quiere x1000, se queda en x1
        Assert.True(decision.Clamped);

        decision.CancelDesire();

        Assert.False(decision.Clamped);
        Assert.Equal(1.0, decision.DesiredRate(Rails));
        // Aunque luego llegue permiso, ya no sube: la petición se canceló.
        Assert.Equal(-1, decision.Update(WarpMode.Rails, 0, Rails, 1000, WarpMode.Rails, out _));
    }

    [Fact]
    public void RequestedMode_IsRailsAtNormalSpeed()
    {
        var decision = new WarpDecision();
        decision.Update(WarpMode.Physics, 0, Physics, 1, WarpMode.Rails, out _);
        Assert.Equal(WarpMode.Rails, decision.RequestedMode(Physics));

        decision.Update(WarpMode.Physics, 2, Physics, 1, WarpMode.Rails, out _);
        Assert.Equal(WarpMode.Physics, decision.RequestedMode(Physics));
    }
}

public class ClockTests
{
    [Fact]
    public void SymmetricLatency_GivesExactOffset()
    {
        var sync = new ClockSync();
        // Reloj del servidor = local + 900; 100 ms de ida y vuelta.
        sync.AddSample(localSendTime: 100.0, localReceiveTime: 100.1, serverTime: 1000.05);

        Assert.Equal(900.0, sync.Offset, 9);
        Assert.Equal(0.1, sync.RoundTripTime, 9);
    }

    [Fact]
    public void LowestRoundTripSample_Wins()
    {
        var sync = new ClockSync();
        sync.AddSample(10.0, 10.5, 910.4); // lenta y asimétrica
        sync.AddSample(20.0, 20.02, 920.01); // rápida y exacta
        sync.AddSample(30.0, 30.3, 930.25); // lenta

        Assert.Equal(900.0, sync.Offset, 9);
    }

    [Fact]
    public void Seed_OnlyAppliesBeforeFirstSample()
    {
        var sync = new ClockSync();
        sync.SeedIfUnsynchronized(5, 105);
        Assert.Equal(100, sync.Offset);

        sync.AddSample(10, 10, 210);
        sync.SeedIfUnsynchronized(5, 5);
        Assert.Equal(200, sync.Offset);
    }

    [Fact]
    public void SharedClock_ExtrapolatesWithRate()
    {
        var clock = new SharedClock();
        Assert.True(double.IsNaN(clock.EstimateUniversalTime(0)));

        clock.AddPong(new PongMessage { ClientTime = 1.0, ServerTime = 10.0 }, 1.0);
        clock.ApplyState(new TimeStateMessage { UniversalTime = 5000, ServerTime = 10.0, Rate = 100 }, 1.0);

        Assert.Equal(5000, clock.EstimateUniversalTime(1.0), 6);
        Assert.Equal(5100, clock.EstimateUniversalTime(2.0), 6);
        Assert.Equal(5000, clock.EstimateUniversalTime(0.5), 6); // nunca antes del último estado
    }

    [Theory]
    [InlineData(0, "Año 1, día 1, 00:00:00")]
    [InlineData(3661, "Año 1, día 1, 01:01:01")]
    [InlineData(21600 * 426 + 21600 + 3661, "Año 2, día 2, 01:01:01")]
    public void KerbalTime_UsesKerbinCalendar(double ut, string expected)
    {
        Assert.Equal(expected, KerbalTime.Format(ut));
    }
}
