using System;

namespace MultiKerbal.Common.Time
{
    /// <summary>Calendario de Kerbin: días de 6 horas y años de 426 días.</summary>
    public static class KerbalTime
    {
        public const long SecondsPerDay = 6 * 3600;
        public const long DaysPerYear = 426;

        public static string Format(double universalTime)
        {
            if (double.IsNaN(universalTime) || double.IsInfinity(universalTime))
                return "—";

            long totalSeconds = (long)Math.Floor(Math.Max(0.0, universalTime));
            long totalDays = totalSeconds / SecondsPerDay;
            long year = totalDays / DaysPerYear + 1;
            long day = totalDays % DaysPerYear + 1;
            long secondsOfDay = totalSeconds % SecondsPerDay;
            return $"Año {year}, día {day}, {secondsOfDay / 3600:00}:{secondsOfDay / 60 % 60:00}:{secondsOfDay % 60:00}";
        }
    }
}
