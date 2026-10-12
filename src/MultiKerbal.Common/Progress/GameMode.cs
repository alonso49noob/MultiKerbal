namespace MultiKerbal.Common.Progress
{
    /// <summary>Tipo de partida del servidor: decide qué progreso se comparte.</summary>
    public enum GameMode : byte
    {
        /// <summary>Sin progreso: todo desbloqueado, sin fondos ni ciencia.</summary>
        Sandbox = 0,

        /// <summary>Ciencia y tecnologías compartidas.</summary>
        Science = 1,

        /// <summary>Ciencia, tecnologías, fondos y reputación compartidos.</summary>
        Career = 2,
    }

    /// <summary>Qué cambia en un <see cref="Messages.ProgressChangeMessage"/>.</summary>
    public enum ProgressKind : byte
    {
        Funds = 0,
        Science = 1,
        Reputation = 2,

        /// <summary>Tecnología investigada (su coste en ciencia llega aparte, como un cambio de ciencia).</summary>
        Tech = 3,

        /// <summary>Pieza comprada en carrera (su precio llega aparte, como un cambio de fondos).</summary>
        Part = 4,

        /// <summary>Edificio mejorado: el nivel (0..1) va en Amount.</summary>
        Facility = 5,
    }

    public static class GameModes
    {
        public static bool IsValid(GameMode mode) => mode == GameMode.Sandbox || mode == GameMode.Science || mode == GameMode.Career;

        public static bool SharesScience(GameMode mode) => mode == GameMode.Science || mode == GameMode.Career;

        public static bool SharesMoney(GameMode mode) => mode == GameMode.Career;

        /// <summary>Lo que se aplica de un cambio en este modo (en sandbox, nada).</summary>
        public static bool Applies(GameMode mode, ProgressKind kind)
        {
            switch (kind)
            {
                case ProgressKind.Science:
                case ProgressKind.Tech:
                    return SharesScience(mode);
                case ProgressKind.Funds:
                case ProgressKind.Reputation:
                case ProgressKind.Part:
                case ProgressKind.Facility:
                    return SharesMoney(mode);
                default:
                    return false;
            }
        }

        public static string Describe(GameMode mode)
        {
            switch (mode)
            {
                case GameMode.Science:
                    return Lang.T("ciencia", "science");
                case GameMode.Career:
                    return Lang.T("carrera", "career");
                default:
                    return "sandbox";
            }
        }
    }
}
