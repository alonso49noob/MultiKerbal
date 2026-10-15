namespace MultiKerbal.Common.Relay
{
    /// <summary>
    /// Limpia los nombres de servidor que vienen de fuera antes de mostrarlos o escribirlos en un registro:
    /// sin caracteres de control (un salto de línea falsificaría una línea del registro), sin espacios en los
    /// extremos y con una longitud máxima.
    /// </summary>
    public static class ListedName
    {
        public static string Sanitize(string name) => Sanitize(name, ProtocolInfo.MaxListedNameLength);

        public static string Sanitize(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (!char.IsControl(c))
                    builder.Append(c);
            }

            string clean = builder.ToString().Trim();
            if (clean.Length > maxLength)
            {
                int length = maxLength;

                // No partir un par suplente (un emoji) por la mitad.
                if (length > 0 && char.IsHighSurrogate(clean[length - 1]))
                    length--;

                clean = clean.Substring(0, length).TrimEnd();
            }

            return clean;
        }
    }
}
