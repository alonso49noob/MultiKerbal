namespace MultiKerbal.Common
{
    /// <summary>
    /// Idioma de los textos que salen de la biblioteca compartida (motivos del warp, errores de red, fechas).
    /// Lo fija el cliente con el ajuste del jugador y el servidor con el suyo.
    /// </summary>
    public static class Lang
    {
        public static bool English { get; set; }

        public static string T(string spanish, string english) => English ? english : spanish;
    }
}
