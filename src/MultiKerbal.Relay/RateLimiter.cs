namespace MultiKerbal.Relay;

/// <summary>
/// Cubo de fichas para el tráfico de una sesión en un sentido: se rellena a <c>bytesPerSecond</c> y guarda como
/// mucho un segundo de crédito. Seguro entre hilos.
/// </summary>
internal sealed class RateLimiter
{
    private readonly double _rate;
    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private double _tokens;
    private double _last;

    /// <param name="clock">Reloj en segundos (los tests usan uno falso).</param>
    public RateLimiter(long bytesPerSecond, Func<double> clock)
    {
        if (bytesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));

        _rate = bytesPerSecond;
        _clock = clock;
        _tokens = _rate;
        _last = clock();
    }

    /// <summary>
    /// Si hay fichas para <paramref name="bytes"/>, las gasta y devuelve cero. Si no, no gasta nada y devuelve
    /// cuánto esperar antes de volver a pedirlas. Un bloque mayor que el cubo pasa en cuanto el cubo está lleno
    /// (y deja deuda), para que nunca espere para siempre.
    /// </summary>
    public TimeSpan Reserve(int bytes)
    {
        lock (_lock)
        {
            double now = _clock();
            _tokens = Math.Min(_rate, _tokens + Math.Max(0, now - _last) * _rate);
            _last = now;

            double needed = Math.Min(bytes, _rate);
            if (_tokens >= needed)
            {
                _tokens -= bytes;
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds((needed - _tokens) / _rate);
        }
    }
}
