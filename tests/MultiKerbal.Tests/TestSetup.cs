using System.Runtime.CompilerServices;

namespace MultiKerbal.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Las pruebas esperan bloqueando (Thread.Sleep, lecturas con plazo) en hilos del pool, mientras los servidores y
    /// relays de prueba necesitan ese mismo pool para su red. Con el mínimo por defecto (uno por núcleo), cuando corren
    /// muchas a la vez el pool tarda en crecer y las pruebas con plazos cortos fallan sin motivo.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 128), Math.Max(io, 128));
    }
}
