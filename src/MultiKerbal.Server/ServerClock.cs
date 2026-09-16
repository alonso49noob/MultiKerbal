using System.Diagnostics;

namespace MultiKerbal.Server;

/// <summary>Reloj monotónico del servidor en segundos. Es la base de ServerTime en el protocolo.</summary>
internal sealed class ServerClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public double Now => _stopwatch.Elapsed.TotalSeconds;
}
