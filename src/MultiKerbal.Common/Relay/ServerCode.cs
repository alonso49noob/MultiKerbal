using System;
using System.Text;

namespace MultiKerbal.Common.Relay
{
    /// <summary>
    /// Código de servidor de 6 caracteres (por ejemplo K7M-Q2P) con el que los jugadores entran a través del relay.
    /// Se guarda sin guion y en mayúsculas; el guion es solo para mostrarlo.
    /// </summary>
    public static class ServerCode
    {
        /// <summary>Sin I, L, O, 0 ni 1 para que no se confundan al dictarlos.</summary>
        public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        public const int Length = 6;

        /// <summary>
        /// Tiene pinta de código: sin espacios ni guiones quedan 6 letras o dígitos, con al menos una letra y un dígito.
        /// Sirve para distinguirlo de una dirección ("gamepc", "192.168.1.5"); no comprueba el alfabeto.
        /// </summary>
        public static bool LooksLikeCode(string input)
        {
            if (input == null)
                return false;

            int count = 0;
            bool hasLetter = false;
            bool hasDigit = false;
            foreach (char c in input)
            {
                if (c == ' ' || c == '-')
                    continue;

                if (c >= '0' && c <= '9')
                    hasDigit = true;
                else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    hasLetter = true;
                else
                    return false; // también descarta '.' y ':' (direcciones)

                count++;
            }

            return count == Length && hasLetter && hasDigit;
        }

        /// <summary>Pasa a mayúsculas y quita espacios y guiones. Falla si no es un código válido del alfabeto.</summary>
        public static bool TryNormalize(string input, out string code)
        {
            code = string.Empty;
            if (!LooksLikeCode(input))
                return false;

            var builder = new StringBuilder(Length);
            foreach (char c in input)
            {
                if (c == ' ' || c == '-')
                    continue;

                char upper = char.ToUpperInvariant(c);
                if (Alphabet.IndexOf(upper) < 0)
                    return false;

                builder.Append(upper);
            }

            code = builder.ToString();
            return true;
        }

        /// <summary>"K7MQ2P" pasa a "K7M-Q2P".</summary>
        public static string Format(string code)
        {
            if (code == null || code.Length != Length)
                return code;

            return code.Substring(0, 3) + "-" + code.Substring(3);
        }

        /// <summary>
        /// Genera un código. <paramref name="nextIndex"/>(n) devuelve un número de 0 a n-1 (el servidor pasa un
        /// generador criptográfico). Repite hasta que haya al menos una letra y un dígito.
        /// </summary>
        public static string Generate(Func<int, int> nextIndex)
        {
            if (nextIndex == null)
                throw new ArgumentNullException(nameof(nextIndex));

            var chars = new char[Length];
            while (true)
            {
                bool hasLetter = false;
                bool hasDigit = false;
                for (int i = 0; i < Length; i++)
                {
                    char c = Alphabet[nextIndex(Alphabet.Length)];
                    chars[i] = c;
                    if (c >= '0' && c <= '9')
                        hasDigit = true;
                    else
                        hasLetter = true;
                }

                if (hasLetter && hasDigit)
                    return new string(chars);
            }
        }
    }
}
