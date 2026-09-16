using System.Text.Json;

namespace MultiKerbal.Server.Persistence;

internal sealed class UniverseData
{
    public double UniversalTime { get; set; }
}

/// <summary>Guarda el universo en disco. Escritura atómica (archivo temporal + renombrado).</summary>
internal sealed class UniverseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public UniverseStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
    }

    public string DirectoryPath { get; }

    private string UniverseFile => Path.Combine(DirectoryPath, "universe.json");

    public UniverseData Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(UniverseFile))
            return new UniverseData();

        return JsonSerializer.Deserialize<UniverseData>(File.ReadAllText(UniverseFile), JsonOptions) ?? new UniverseData();
    }

    public void Save(UniverseData data)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temp = UniverseFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, UniverseFile, overwrite: true);
    }
}
