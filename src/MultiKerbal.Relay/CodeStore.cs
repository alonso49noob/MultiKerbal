using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiKerbal.Common.Relay;

namespace MultiKerbal.Relay;

/// <summary>
/// Códigos permanentes de los servidores: a cada clave pública le corresponde siempre el mismo código.
/// Se guarda en codes.json solo el SHA-256 de la clave (nunca la clave) junto al código y si está baneado.
/// Escritura con archivo temporal y renombrado, sin el cerrojo de las búsquedas. Seguro entre hilos.
/// </summary>
internal sealed class CodeStore
{
    private const int FileVersion = 1;
    private const string FileName = "codes.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();

    /// <summary>Una sola escritura del archivo a la vez. Se toma antes que <see cref="_lock"/>, nunca al revés.</summary>
    private readonly object _saveLock = new();
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
        string code = Reserve(key, out bool isNew);
        if (isNew)
        {
            try
            {
                Persist(code);
            }
            catch
            {
                Forget(code);
                throw;
            }
        }

        return code;
    }

    /// <summary>
    /// Devuelve el código de esta clave; si es nueva, le asigna uno sin repetir solo en memoria (<paramref name="isNew"/>).
    /// Quien reserva un código nuevo lo guarda después con <see cref="Persist"/>, fuera de sus propios cerrojos, y si
    /// no se puede guardar lo suelta con <see cref="Forget"/>.
    /// </summary>
    public string Reserve(byte[] key, out bool isNew)
    {
        string hash = Convert.ToHexString(SHA256.HashData(key));
        lock (_lock)
        {
            if (_byKeyHash.TryGetValue(hash, out Entry? existing))
            {
                isNew = false;
                return existing.Code;
            }

            string code;
            do
            {
                code = ServerCode.Generate(RandomNumberGenerator.GetInt32);
            }
            while (_byCode.ContainsKey(code));

            var entry = new Entry { KeyHash = hash, Code = code };
            _byKeyHash[hash] = entry;
            _byCode[code] = entry;
            isNew = true;
            return code;
        }
    }

    /// <summary>
    /// Se asegura de que el código (reservado con <see cref="Reserve"/>) está en el disco: si ya lo escribió otro
    /// guardado, no hace nada. Lanza si no se puede escribir.
    /// </summary>
    public void Persist(string code)
    {
        lock (_saveLock)
        {
            List<Entry> snapshot;
            lock (_lock)
            {
                if (!_byCode.TryGetValue(code, out Entry? entry) || entry.Persisted)
                    return;

                snapshot = Snapshot();
            }

            // El disco se escribe sin el cerrojo de los mapas: las búsquedas no esperan a la escritura.
            Write(snapshot);

            lock (_lock)
            {
                foreach (Entry saved in snapshot)
                {
                    if (_byCode.TryGetValue(saved.Code, out Entry? live))
                        live.Persisted = true;
                }
            }
        }
    }

    /// <summary>Suelta un código reservado que no se pudo guardar (si ya está en el disco, se queda).</summary>
    public void Forget(string code)
    {
        lock (_lock)
        {
            if (_byCode.TryGetValue(code, out Entry? entry) && !entry.Persisted)
            {
                _byCode.Remove(code);
                _byKeyHash.Remove(entry.KeyHash);
            }
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
        lock (_saveLock)
        {
            Entry? entry;
            List<Entry> snapshot;
            lock (_lock)
            {
                entry = Find(code);
                if (entry == null)
                    return false;

                if (entry.Banned == banned)
                    return true;

                entry.Banned = banned;
                snapshot = Snapshot();
            }

            try
            {
                Write(snapshot);
            }
            catch
            {
                lock (_lock)
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
            entry.Persisted = true;
            _byKeyHash[entry.KeyHash] = entry;
            _byCode[code] = entry;
        }
    }

    /// <summary>Copia de todas las entradas para escribirla. Con <see cref="_lock"/>.</summary>
    private List<Entry> Snapshot() =>
        _byCode.Values
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .Select(e => new Entry { KeyHash = e.KeyHash, Code = e.Code, Banned = e.Banned })
            .ToList();

    /// <summary>Escribe el archivo (temporal y renombrado). Con <see cref="_saveLock"/>, para que no se pisen dos escrituras.</summary>
    private void Write(List<Entry> entries)
    {
        var data = new FileData { Version = FileVersion, Codes = entries };
        string temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class Entry
    {
        public string KeyHash { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public bool Banned { get; set; }

        /// <summary>Ya está en el disco (las cargadas de codes.json, o escritas por un guardado).</summary>
        [JsonIgnore]
        public bool Persisted { get; set; }
    }

    private sealed class FileData
    {
        public int Version { get; set; }

        public List<Entry> Codes { get; set; } = [];
    }
}
