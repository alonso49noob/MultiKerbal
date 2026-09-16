using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Time;

namespace MultiKerbal.Server.Systems;

/// <summary>Reloj universal autoritativo: UT(t) = base + (t − t_base) × rate, rebasado en cada cambio.</summary>
internal sealed class TimeSystem
{
    private double _baseUniversalTime;
    private double _baseServerTime;

    public double Rate { get; private set; } = 1.0;

    public WarpMode Mode { get; private set; } = WarpMode.Rails;

    public string LimitedBy { get; private set; } = string.Empty;

    public bool LimiterAutoDenies { get; private set; }

    public bool Paused { get; private set; } = true;

    public void Initialize(double universalTime, double now)
    {
        _baseUniversalTime = universalTime;
        _baseServerTime = now;
    }

    public double CurrentUniversalTime(double now) =>
        Paused ? _baseUniversalTime : _baseUniversalTime + (now - _baseServerTime) * Rate;

    public void Pause(double now)
    {
        if (Paused)
            return;

        Rebase(now);
        Paused = true;
    }

    public void Resume(double now)
    {
        if (!Paused)
            return;

        _baseServerTime = now;
        Paused = false;
    }

    /// <summary>Devuelve true si el estado cambió y hay que difundirlo.</summary>
    public bool ApplyConsensus(WarpConsensusResult result, double now)
    {
        string limitedBy = result.LimitedBy ?? string.Empty;
        if (result.Rate == Rate && result.Mode == Mode && limitedBy == LimitedBy && result.LimiterAutoDenies == LimiterAutoDenies)
            return false;

        Rebase(now);
        Rate = result.Rate;
        Mode = result.Mode;
        LimitedBy = limitedBy;
        LimiterAutoDenies = result.LimiterAutoDenies;
        return true;
    }

    public TimeStateMessage BuildState(double now) => new()
    {
        UniversalTime = CurrentUniversalTime(now),
        ServerTime = now,
        Rate = Paused ? 0.0 : Rate,
        Mode = Mode,
        LimitedBy = LimitedBy,
        LimiterAutoDenies = LimiterAutoDenies,
    };

    private void Rebase(double now)
    {
        _baseUniversalTime = CurrentUniversalTime(now);
        _baseServerTime = now;
    }
}
