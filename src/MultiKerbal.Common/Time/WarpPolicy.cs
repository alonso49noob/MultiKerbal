using System;

namespace MultiKerbal.Common.Time
{
    /// <summary>Qué hace un jugador cuando otro pide acelerar el tiempo.</summary>
    public sealed class WarpPolicy
    {
        public bool AutoAccept;

        /// <summary>Warp máximo que se acepta sin pedirlo.</summary>
        public double AcceptMaxRate = 1000;

        public bool AcceptOnlyOutsideFlight;
        public bool AcceptOnlyStable = true;
        public bool AcceptOnlyEnginesOff = true;
        public bool AcceptOnlyAlone = true;

        /// <summary>Aceptar cuando el jugador lleva un rato sin tocar nada, sin mirar las demás condiciones.</summary>
        public bool AcceptWhenIdle;

        /// <summary>Segundos sin teclado ni ratón para darlo por ausente.</summary>
        public double IdleSeconds = 120;

        public bool AutoDeny;
        public bool DenyWhileFlying;
        public bool DenyAtSpaceCenter;
        public bool DenyInAtmosphere = true;
        public bool DenyNearVessels = true;
    }

    /// <summary>Situación local del jugador en este instante.</summary>
    public struct WarpContext
    {
        /// <summary>Pilotando una nave (escena de vuelo).</summary>
        public bool InFlight;

        /// <summary>En la pantalla del Centro Espacial.</summary>
        public bool AtSpaceCenter;

        /// <summary>En órbita, posada o amerizada (siempre true fuera del vuelo).</summary>
        public bool StableSituation;

        public bool EnginesOff;
        public bool InAtmosphere;

        /// <summary>Otras naves, sin contar banderas ni escombros, a menos de 2,5 km.</summary>
        public bool VesselsNearby;

        /// <summary>TimeWarp en modo sobre raíles.</summary>
        public bool RailsMode;

        /// <summary>Máximo warp sobre raíles que KSP permite ahora mismo (altitud, atmósfera).</summary>
        public double MaxRailsRate;

        /// <summary>Tiempo sin tocar teclado ni ratón.</summary>
        public double IdleSeconds;
    }

    public struct WarpPolicyDecision
    {
        public bool AutoDeny;

        /// <summary>Warp de los demás que se acepta automáticamente (1 = no se acepta).</summary>
        public double AcceptUpTo;

        /// <summary>Motivo, para explicárselo al jugador.</summary>
        public string Reason;
    }

    /// <summary>
    /// Aplica los ajustes de warp a la situación actual. Rechazar manda: mientras esté activo no se acepta nada
    /// automáticamente, y basta con que se cumpla una de sus condiciones (sin ninguna marcada, siempre).
    /// Para aceptar tienen que cumplirse todas las condiciones marcadas, salvo estando ausente.
    /// </summary>
    public static class WarpPolicyEvaluator
    {
        private const double MinIdleSeconds = 5.0;

        public static WarpPolicyDecision Evaluate(WarpPolicy policy, WarpContext context)
        {
            if (policy.AutoDeny)
            {
                string denyReason = DenyReason(policy, context);
                if (denyReason != null)
                    return new WarpPolicyDecision { AutoDeny = true, AcceptUpTo = 1.0, Reason = denyReason };

                // Con el rechazo activado nunca se acepta automáticamente: si ahora no se cumple ninguna de sus
                // condiciones, simplemente decide el jugador.
                return NotAccepting(Lang.T("no se cumple ninguna condición de rechazo: decides tú", "none of the deny conditions apply: it is up to you"));
            }

            if (!policy.AutoAccept && !policy.AcceptWhenIdle)
                return NotAccepting(Lang.T("la aceptación automática está desactivada", "automatic accept is off"));

            bool idle = policy.AcceptWhenIdle && context.IdleSeconds >= Math.Max(MinIdleSeconds, policy.IdleSeconds);
            string blocker = idle ? IdleBlocker(context) : AcceptBlocker(policy, context);
            if (blocker != null)
                return NotAccepting(blocker);

            double acceptUpTo = Math.Min(Math.Max(1.0, policy.AcceptMaxRate), context.MaxRailsRate);
            if (acceptUpTo <= 1.0)
                return NotAccepting(Lang.T("KSP no permite warp sobre raíles aquí", "KSP does not allow on-rails warp here"));

            return new WarpPolicyDecision
            {
                AcceptUpTo = acceptUpTo,
                Reason = idle
                    ? Lang.T($"llevas {FormatSeconds(context.IdleSeconds)} sin tocar nada", $"you have been away for {FormatSeconds(context.IdleSeconds)}")
                    : Lang.T("se cumplen las condiciones", "the conditions are met"),
            };
        }

        public static string FormatSeconds(double seconds) =>
            seconds < 60.0 ? $"{seconds:0} s" : $"{seconds / 60.0:0.#} min";

        private static string DenyReason(WarpPolicy policy, WarpContext context)
        {
            if (!policy.DenyWhileFlying && !policy.DenyAtSpaceCenter && !policy.DenyInAtmosphere && !policy.DenyNearVessels)
                return Lang.T("rechazo automático sin condiciones", "automatic deny, no conditions");
            if (policy.DenyWhileFlying && context.InFlight)
                return Lang.T("estás pilotando", "you are flying");
            if (policy.DenyAtSpaceCenter && context.AtSpaceCenter)
                return Lang.T("estás en el Centro Espacial", "you are at the Space Center");
            if (policy.DenyInAtmosphere && context.InAtmosphere)
                return Lang.T("tu nave está en la atmósfera", "your vessel is in the atmosphere");
            if (policy.DenyNearVessels && context.VesselsNearby)
                return Lang.T("hay otras naves cerca", "there are other vessels nearby");
            return null;
        }

        /// <summary>Estando ausente da igual lo que haga la nave: solo importa que KSP permita warp sobre raíles.</summary>
        private static string IdleBlocker(WarpContext context) =>
            context.RailsMode ? null : Lang.T("estás usando warp físico", "you are using physics warp");

        private static string AcceptBlocker(WarpPolicy policy, WarpContext context)
        {
            if (!policy.AutoAccept)
                return Lang.T(
                    $"llevas menos de {FormatSeconds(Math.Max(MinIdleSeconds, policy.IdleSeconds))} sin tocar nada",
                    $"you have been away for less than {FormatSeconds(Math.Max(MinIdleSeconds, policy.IdleSeconds))}");
            if (!context.RailsMode)
                return Lang.T("estás usando warp físico", "you are using physics warp");
            if (policy.AcceptOnlyOutsideFlight && context.InFlight)
                return Lang.T("estás pilotando", "you are flying");
            if (policy.AcceptOnlyStable && !context.StableSituation)
                return Lang.T("tu nave no está en órbita ni posada", "your vessel is neither in orbit nor landed");
            if (policy.AcceptOnlyEnginesOff && !context.EnginesOff)
                return Lang.T("los motores están encendidos", "the engines are running");
            if (policy.AcceptOnlyAlone && context.VesselsNearby)
                return Lang.T("hay otras naves cerca", "there are other vessels nearby");
            return null;
        }

        private static WarpPolicyDecision NotAccepting(string reason) =>
            new WarpPolicyDecision { AcceptUpTo = 1.0, Reason = reason };
    }
}
