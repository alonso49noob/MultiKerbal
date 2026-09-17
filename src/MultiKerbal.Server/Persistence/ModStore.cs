using System.Text.Encodings.Web;
using System.Text.Json;
using MultiKerbal.Common;
using MultiKerbal.Common.Mods;

namespace MultiKerbal.Server.Persistence;

/// <summary>
/// Los mods que el servidor espera, en <c>Universe/mods.json</c>. El servidor no tiene KSP, así que la lista es la
/// del primer jugador que entra (o la que fije el administrador con el comando <c>mods</c>).
/// </summary>
internal sealed class ModStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public ModStore(string universeDirectory)
    {
        _path = Path.Combine(Path.GetFullPath(universeDirectory), "mods.json");
    }

    public ModInfo[] Mods { get; private set; } = [];

    /// <summary>Vacía hasta que entra el primer jugador.</summary>
    public bool HasReference => Mods.Length > 0;

    /// <summary>Nombre del jugador del que salió la lista (solo informativo).</summary>
    public string Source { get; private set; } = string.Empty;

    public void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            StoredMods stored = JsonSerializer.Deserialize<StoredMods>(File.ReadAllText(_path), JsonOptions) ?? new StoredMods();
            Mods = stored.Mods.Select(m => new ModInfo { Name = m.Name ?? string.Empty, Version = m.Version ?? string.Empty })
                .Where(m => m.Name.Length > 0)
                .Take(ProtocolInfo.MaxMods)
                .ToArray();
            Source = stored.Source ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"No se pudo leer {_path}: {ex.Message}. El servidor tomará la lista del próximo jugador.");
        }
    }

    public void Set(ModInfo[] mods, string source)
    {
        Mods = (mods ?? [])
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .Select(m => new ModInfo
            {
                Name = ServerHost.SanitizeText(m.Name, ProtocolInfo.MaxModNameLength),
                Version = ServerHost.SanitizeText(m.Version, ProtocolInfo.MaxModNameLength),
            })
            .Take(ProtocolInfo.MaxMods)
            .ToArray();
        Source = source;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var stored = new StoredMods
            {
                Source = Source,
                Mods = Mods.Select(m => new StoredMod { Name = m.Name, Version = m.Version }).ToList(),
            };
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn($"No se pudo guardar {_path}: {ex.Message}");
        }
    }

    private sealed class StoredMods
    {
        public string Source { get; set; } = string.Empty;

        public List<StoredMod> Mods { get; set; } = [];
    }

    private sealed class StoredMod
    {
        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;
    }
}
