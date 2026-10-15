using System.Diagnostics;

namespace MultiKerbal.Relay;

/// <summary>
/// Cubo de fichas: se rellena a un ritmo fijo y guarda como mucho <c>capacity</c> fichas. Sirve para el tráfico de una
/// sesión en un sentido (fichas = bytes, con un segundo de crédito) y para las conexiones nuevas de una IP
/// (fichas = conexiones). Seguro entre hilos.
/// </summary>
internal sealed class RateLimiter
{
    private readonly double _rate;
    private readonly double _capacity;
    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private double _tokens;
    private double _last;

    /// <summary>Reloj monótono en segundos, el que usa el relay de verdad.</summary>
    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <param name="clock">Reloj en segundos (los tests usan uno falso).</param>
    public RateLimiter(long bytesPerSecond, Func<double> clock)
        : this(bytesPerSecond, bytesPerSecond, clock)
    {
    }

    /// <param name="perSecond">Fichas que entran por segundo.</param>
    /// <param name="capacity">Fichas que caben (la ráfaga máxima). Empieza lleno.</param>
    /// <param name="clock">Reloj en segundos (los tests usan uno falso).</param>
    public RateLimiter(double perSecond, double capacity, Func<double> clock)
    {
        if (perSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(perSecond));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _rate = perSecond;
        _capacity = capacity;
        _clock = clock;
        _tokens = capacity;
        _last = clock();
    }

    /// <summary>El cubo está lleno: gastar de él ahora es igual que de uno nuevo (para olvidar los que no se usan).</summary>
    public bool IsFull
    {
        get
        {
            lock (_lock)
                return Math.Min(_capacity, _tokens + Math.Max(0, _clock() - _last) * _rate) >= _capacity;
        }
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
            _tokens = Math.Min(_capacity, _tokens + Math.Max(0, now - _last) * _rate);
            _last = now;

            double needed = Math.Min(bytes, _capacity);
            if (_tokens >= needed)
            {
                _tokens -= bytes;
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds((needed - _tokens) / _rate);
        }
    }
}
