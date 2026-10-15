namespace MultiKerbal.Server;

/// <summary>
/// Deja pasar un aviso del registro como mucho una vez por intervalo, y cuenta los que se callan. Para avisos que
/// cualquiera puede provocar a voluntad. No es seguro entre hilos: se usa desde el bucle principal.
/// </summary>
internal sealed class WarningLimiter
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _clock;
    private DateTime _last = DateTime.MinValue;
    private int _skipped;

    /// <param name="clock">Reloj (UTC); los tests usan uno falso.</param>
    public WarningLimiter(TimeSpan interval, Func<DateTime>? clock = null)
    {
        _interval = interval;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True si toca escribir el aviso; <paramref name="skipped"/> dice cuántos se callaron desde el último.</summary>
    public bool Allow(out int skipped)
    {
        DateTime now = _clock();
        if (now - _last < _interval)
        {
            _skipped++;
            skipped = 0;
            return false;
        }

        skipped = _skipped;
        _skipped = 0;
        _last = now;
        return true;
    }
}
