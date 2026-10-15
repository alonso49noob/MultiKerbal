using System.Net;

namespace MultiKerbal.Relay;

/// <summary>
/// Cuántas claves nuevas acepta el relay de cada IP: cada una gasta un código para siempre y escribe codes.json, así
/// que una IP no puede pedir más de <see cref="RelayOptions.MaxNewCodesPerIp"/> en <see cref="RelayOptions.NewCodeWindow"/>.
/// Solo en memoria (se reinicia con el relay). Volver a registrar una clave conocida no gasta nada. Seguro entre hilos.
/// </summary>
internal sealed class NewCodeBudget
{
    private readonly int _max;
    private readonly double _window;
    private readonly Func<double> _clock;
    private readonly object _lock = new();

    /// <summary>Momentos de las claves nuevas aceptadas de cada IP dentro de la ventana, del más viejo al más nuevo.</summary>
    private readonly Dictionary<IPAddress, Queue<double>> _byAddress = new();
    private double _nextSweep;

    /// <param name="clock">Reloj en segundos (los tests usan uno falso).</param>
    public NewCodeBudget(int max, TimeSpan window, Func<double> clock)
    {
        _max = max;
        _window = window.TotalSeconds;
        _clock = clock;
        _nextSweep = clock() + SweepSeconds;
    }

    /// <summary>IP que se recuerdan ahora (para los tests).</summary>
    public int TrackedCount
    {
        get
        {
            lock (_lock)
                return _byAddress.Count;
        }
    }

    private double SweepSeconds => Math.Min(_window, 600);

    /// <summary>Gasta una clave nueva de esa IP. False si ya gastó todas las de la ventana.</summary>
    public bool TryTake(IPAddress address)
    {
        lock (_lock)
        {
            double now = _clock();
            Sweep(now);

            if (!_byAddress.TryGetValue(address, out Queue<double>? times))
            {
                times = new Queue<double>();
                _byAddress[address] = times;
            }

            Expire(times, now);
            if (times.Count >= _max)
                return false;

            times.Enqueue(now);
            return true;
        }
    }

    private void Expire(Queue<double> times, double now)
    {
        while (times.Count > 0 && now - times.Peek() >= _window)
            times.Dequeue();
    }

    private void Sweep(double now)
    {
        if (now < _nextSweep)
            return;

        _nextSweep = now + SweepSeconds;
        foreach (KeyValuePair<IPAddress, Queue<double>> pair in _byAddress.ToArray())
        {
            Expire(pair.Value, now);
            if (pair.Value.Count == 0)
                _byAddress.Remove(pair.Key);
        }
    }
}
