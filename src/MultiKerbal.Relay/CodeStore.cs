using System.Security.Cryptography;
using System.Text.Json;
using MultiKerbal.Common.Relay;

namespace MultiKerbal.Relay;

/// <summary>
/// Códigos permanentes de los servidores: a cada clave pública le corresponde siempre el mismo código.
/// Se guarda en codes.json solo el SHA-256 de la clave (nunca la clave) junto al código y si está baneado.
/// Escritura con archivo temporal y renombrado. Seguro entre hilos.
/// </summary>
internal sealed class CodeStore
{
    private const int FileVersion = 1;
    private const string FileName = "codes.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _path;

    /// <summary>SHA-256 de la clave en hexadecimal → entrada.</summary>
    private readonly Dictionary<string, Entry> _byKeyHash = new();

    /// <summary>Código (sin guion) → entrada.</summary>
    private readonly Dictionary<string, Entry> _byCode = new();

    public CodeStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, FileName);
        Directory.CreateDirectory(dataDirectory);
        Load();
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _byCode.Count;
        }
    }

    /// <summary>Devuelve el código de esta clave; si es nueva, le asigna uno sin repetir y lo guarda.</summary>
    public string GetOrAssign(byte[] key)
    {
        string hash = Convert.ToHexString(SHA256.HashData(key));
        lock (_lock)
        {
            if (_byKeyHash.TryGetValue(hash, out Entry? existing))
                return existing.Code;

            string code;
            do
            {
                code = ServerCode.Generate(RandomNumberGenerator.GetInt32);
            }
            while (_byCode.ContainsKey(code));

            var entry = new Entry { KeyHash = hash, Code = code };
            _byKeyHash[hash] = entry;
            _byCode[code] = entry;
            try
            {
                Save();
            }
            catch
            {
                // Que lo que hay en memoria no se adelante a lo que hay en disco.
                _byKeyHash.Remove(hash);
                _byCode.Remove(code);
                throw;
            }

            return code;
        }
    }

    /// <summary>Busca el código de esta clave sin asignar ninguno. False si la clave es nueva.</summary>
    public bool TryGetCode(byte[] key, out string code)
    {
        string hash = Convert.ToHexString(SHA256.HashData(key));
        lock (_lock)
        {
            if (_byKeyHash.TryGetValue(hash, out Entry? entry))
            {
                code = entry.Code;
                return true;
            }
        }

        code = string.Empty;
        return false;
    }

    /// <summary>Acepta "K7MQ2P" o "K7M-Q2P" (también en minúsculas).</summary>
    public bool IsBanned(string code)
    {
        lock (_lock)
            return Find(code) is { Banned: true };
    }

    /// <summary>Banea un código. Devuelve false si no existe.</summary>
    public bool Ban(string code) => SetBanned(code, true);

    /// <summary>Quita el baneo. Devuelve false si el código no existe.</summary>
    public bool Unban(string code) => SetBanned(code, false);

    private bool SetBanned(string code, bool banned)
    {
        lock (_lock)
        {
            Entry? entry = Find(code);
            if (entry == null)
                return false;

            if (entry.Banned == banned)
                return true;

            entry.Banned = banned;
            try
            {
                Save();
            }
            catch
            {
                entry.Banned = !banned;
                throw;
            }

            return true;
        }
    }

    private Entry? Find(string code)
    {
        if (!ServerCode.TryNormalize(code, out string normalized))
            return null;

        return _byCode.GetValueOrDefault(normalized);
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        FileData? data;
        try
        {
            data = JsonSerializer.Deserialize<FileData>(File.ReadAllBytes(_path), JsonOptions);
        }
        catch (JsonException ex)
        {
            // No se arranca con la lista vacía: se reasignarían códigos que los jugadores ya conocen.
            throw new InvalidDataException($"{FileName} está dañado: {ex.Message}", ex);
        }

        foreach (Entry entry in data?.Codes ?? [])
        {
            if (string.IsNullOrEmpty(entry.KeyHash) || !ServerCode.TryNormalize(entry.Code, out string code))
                continue;

            entry.Code = code;
            _byKeyHash[entry.KeyHash] = entry;
            _byCode[code] = entry;
        }
    }

    private void Save()
    {
        var data = new FileData { Version = FileVersion, Codes = _byCode.Values.OrderBy(e => e.Code, StringComparer.Ordinal).ToList() };
        string temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class Entry
    {
        public string KeyHash { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public bool Banned { get; set; }
    }

    private sealed class FileData
    {
        public int Version { get; set; }

        public List<Entry> Codes { get; set; } = [];
    }
}
