using KSP.Localization;
using MultiKerbal.Common;

namespace MultiKerbal.Client
{
    /// <summary>
    /// Textos en dos idiomas. Cada uno va donde se usa, en español y en inglés, en vez de en un archivo de claves:
    /// así no se queda ningún texto a medio traducir sin que se note al leer el código.
    /// </summary>
    internal static class Loc
    {
        public static bool English { get; private set; }

        /// <summary>Del ajuste del jugador o, si está en automático, del idioma de KSP.</summary>
        public static void Initialize(string setting)
        {
            switch (setting)
            {
                case "es":
                    English = false;
                    break;
                case "en":
                    English = true;
                    break;
                default:
                    English = !IsSpanish(Localizer.CurrentLanguage);
                    break;
            }

            // La biblioteca compartida (motivos del warp, errores de red, fechas) usa el mismo idioma.
            Lang.English = English;
        }

        public static string T(string spanish, string english) => English ? english : spanish;

        private static bool IsSpanish(string language) =>
            !string.IsNullOrEmpty(language) && language.StartsWith("es");
    }
}
