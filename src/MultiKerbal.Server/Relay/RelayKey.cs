using System.Security.Cryptography;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Server.Relay;

/// <summary>
/// Clave secreta del servidor ante el relay: 32 bytes aleatorios en <c>relay.key</c>. Quien tenga ese archivo es
/// ese servidor, y el relay le da siempre el mismo código.
/// </summary>
internal static class RelayKey
{
    public const string FileName = "relay.key";

    /// <summary>
    /// Lee la clave de <paramref name="directory"/> o, si no existe, la crea. Si el archivo está dañado (tamaño
    /// distinto de 32 bytes) lanza <see cref="InvalidDataException"/> sin tocarlo: crear otra clave haría perder
    /// en silencio el código del servidor.
    /// </summary>
    public static byte[] LoadOrCreate(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.Length != RelayRegisterMessage.KeyLength)
                throw new InvalidDataException(
                    $"{path} tiene {existing.Length} bytes y la clave debe tener {RelayRegisterMessage.KeyLength}. " +
                    "Restaura una copia o bórralo (el servidor recibirá otro código).");

            return existing;
        }

        Directory.CreateDirectory(directory);
        byte[] key = RandomNumberGenerator.GetBytes(RelayRegisterMessage.KeyLength);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, key);
        File.Move(temp, path, overwrite: true);
        return key;
    }
}
