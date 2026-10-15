using System;
using System.Threading;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// La red bloquea (y la resolución de nombres no tiene plazo propio): las ventanas nunca la llaman desde el hilo de
    /// Unity. El hilo deja el resultado en un campo volátil y la ventana lo lee al dibujarse.
    /// </summary>
    internal static class BackgroundWork
    {
        public static void Run(string what, Action work, Action done)
        {
            new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    ClientLog.Warn($"Fallo en {what}: {ex.Message}");
                }
                finally
                {
                    done();
                }
            })
            {
                IsBackground = true,
                Name = "MultiKerbal " + what,
            }.Start();
        }
    }
}
