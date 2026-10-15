using System.Net;

namespace MultiKerbal.Relay;

/// <summary>
/// Avisos del registro que cualquiera puede provocar sin identificarse (conexiones rechazadas, mensajes raros...): de
/// cada tipo y cada IP se escribe como mucho uno por intervalo. Los que se callan se cuentan y salen en el siguiente
/// aviso de ese tipo y esa IP, o en un resumen cuando se limpian las entradas viejas. Seguro entre hilos.
/// </summary>
internal sealed class WarningThrottle
{
    private readonly double _interval;
    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<(string Kind, IPAddress? Address), Entry> _entries = new();
    private double _nextSweep;

    /// <param name="clock">Reloj en segundos (los tests usan uno falso).</param>
    public WarningThrottle(TimeSpan interval, Func<double> clock)
    {
        _interval = interval.TotalSeconds;
        _clock = clock;
        _nextSweep = clock() + _interval;
    }

    /// <summary>Entradas que se recuerdan ahora (para los tests: no deben crecer sin límite).</summary>
    public int TrackedCount
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    public void Warn(string kind, IPAddress? address, string message) => Write(kind, address, message, RelayLog.Warn);

    public void Info(string kind, IPAddress? address, string message) => Write(kind, address, message, RelayLog.Info);

    public void Error(string kind, IPAddress? address, string message) => Write(kind, address, message, RelayLog.Error);

    private void Write(string kind, IPAddress? address, string message, Action<string> log)
    {
        var lines = new List<(Action<string> Log, string Line)>();
        lock (_lock)
        {
            double now = _clock();
            Sweep(now, lines);

            var key = (kind, address);
            if (_entries.TryGetValue(key, out Entry? entry) && now - entry.Last < _interval)
            {
                entry.Suppressed++;
                entry.Log = log;
            }
            else
            {
                int suppressed = entry?.Suppressed ?? 0;
                lines.Add((log, suppressed > 0 ? $"{message} (y {suppressed} aviso(s) igual(es) más antes de este)" : message));
                _entries[key] = new Entry { Last = now, Log = log };
            }
        }

        // Fuera del cerrojo: escribir el registro no debe frenar a los demás hilos.
        foreach ((Action<string> write, string line) in lines)
            write(line);
    }

    /// <summary>Cada intervalo, olvida las entradas viejas; las que callaron avisos dejan una línea de resumen.</summary>
    private void Sweep(double now, List<(Action<string> Log, string Line)> lines)
    {
        if (now < _nextSweep)
            return;

        _nextSweep = now + _interval;
        foreach (KeyValuePair<(string Kind, IPAddress? Address), Entry> pair in _entries.ToArray())
        {
            if (now - pair.Value.Last < _interval)
                continue;

            if (pair.Value.Suppressed > 0)
            {
                string from = pair.Key.Address != null ? $" desde {pair.Key.Address}" : string.Empty;
                lines.Add((pair.Value.Log, $"{pair.Value.Suppressed} aviso(s) más de tipo \"{pair.Key.Kind}\"{from} en el último intervalo"));
            }

            _entries.Remove(pair.Key);
        }
    }

    private sealed class Entry
    {
        public double Last;
        public int Suppressed;
        public Action<string> Log = RelayLog.Warn;
    }
}
