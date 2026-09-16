using System;
using System.IO;
using System.IO.Compression;

namespace MultiKerbal.Common.Serialization
{
    /// <summary>GZip. En el Mono de KSP depende de MonoPosixHelper, que viene con el juego.</summary>
    public static class Compression
    {
        public static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress))
                    gzip.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        /// <exception cref="ProtocolException">Datos corruptos o que superan <paramref name="maxBytes"/> al descomprimir.</exception>
        public static byte[] Decompress(byte[] data, int maxBytes = ProtocolInfo.MaxDecompressedBytes)
        {
            try
            {
                using (var input = new MemoryStream(data))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > maxBytes)
                            throw new ProtocolException("Los datos descomprimidos superan el tamaño máximo");
                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                throw new ProtocolException($"Datos comprimidos corruptos: {ex.Message}");
            }
        }
    }
}
