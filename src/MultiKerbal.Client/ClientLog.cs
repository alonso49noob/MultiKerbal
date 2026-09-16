using System.Diagnostics;
using UnityEngine;

namespace MultiKerbal.Client
{
    internal static class ClientLog
    {
        private const string Prefix = "[MultiKerbal] ";

        public static void Info(string message) => UnityEngine.Debug.Log(Prefix + message);

        public static void Warn(string message) => UnityEngine.Debug.LogWarning(Prefix + message);

        public static void Error(string message) => UnityEngine.Debug.LogError(Prefix + message);
    }

    /// <summary>Reloj monotónico local (no depende de Time.timeScale ni de las pausas de KSP).</summary>
    internal static class LocalClock
    {
        private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();

        public static double Now => Stopwatch.Elapsed.TotalSeconds;
    }
}
