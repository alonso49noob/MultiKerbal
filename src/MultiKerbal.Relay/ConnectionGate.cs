using System.Net;

namespace MultiKerbal.Relay;

/// <summary>
/// Límites por IP nada más aceptar una conexión, antes de leer nada: cuántas puede tener abiertas a la vez y a qué ritmo
/// puede abrir nuevas (cubo de fichas). Las IP sin conexiones y con el cubo lleno se olvidan cada cierto tiempo, para
/// que la tabla no crezca sin límite. Seguro entre hilos.
/// </summary>
internal sealed class ConnectionGate
{
    private const double SweepSeconds = 60;

    private readonly RelayOptions _options;
    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = new();
    private double _nextSweep;

    /// <param name="clock">Reloj en segundos (los tests usan uno falso).</param>
    public ConnectionGate(RelayOptions options, Func<double> clock)
    {
        _options = options;
        _clock = clock;
        _nextSweep = clock() + SweepSeconds;
    }

    /// <summary>IP que se recuerdan ahora (para los tests: no deben crecer sin límite).</summary>
    public int TrackedCount
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <summary>
    /// Una conexión nueva de <paramref name="address"/>. Si se admite, cuenta como abierta hasta <see cref="Leave"/>;
    /// si no, <paramref name="refusal"/> dice por qué (para el registro).
    /// </summary>
    public bool TryEnter(IPAddress address, out string refusal)
    {
        lock (_lock)
        {
            double now = _clock();
            Sweep(now);

            if (!_entries.TryGetValue(address, out Entry? entry))
            {
                entry = new Entry(new RateLimiter(_options.NewConnectionsPerSecondPerIp, _options.NewConnectionBurstPerIp, _clock));
                _entries[address] = entry;
            }

            if (entry.Open >= _options.MaxConnectionsPerIp)
            {
                refusal = $"ya tiene {entry.Open} conexiones abiertas";
                return false;
            }

            if (entry.NewConnections.Reserve(1) > TimeSpan.Zero)
            {
                refusal = "abre conexiones demasiado deprisa";
                return false;
            }

            entry.Open++;
            refusal = string.Empty;
            return true;
        }
    }

    /// <summary>Una conexión admitida con <see cref="TryEnter"/> se cierra o deja de contar (control o enganche de un servidor).</summary>
    public void Leave(IPAddress address)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(address, out Entry? entry) && entry.Open > 0)
                entry.Open--;
        }
    }

    private void Sweep(double now)
    {
        if (now < _nextSweep)
            return;

        _nextSweep = now + SweepSeconds;
        foreach (KeyValuePair<IPAddress, Entry> pair in _entries.ToArray())
        {
            // Sin conexiones y con el cubo lleno, una entrada nueva sería idéntica.
            if (pair.Value.Open == 0 && pair.Value.NewConnections.IsFull)
                _entries.Remove(pair.Key);
        }
    }

    private sealed class Entry
    {
        public Entry(RateLimiter newConnections)
        {
            NewConnections = newConnections;
        }

        public RateLimiter NewConnections { get; }

        public int Open { get; set; }
    }
}
