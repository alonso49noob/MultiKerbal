using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Server.Persistence;

internal sealed class StoredVessel
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Versión de estructura enviada por el dueño (ver <see cref="VesselProtoMessage.StructureVersion"/>).</summary>
    public int StructureVersion { get; set; }

    /// <summary>Definición comprimida tal como la envió el cliente (el servidor no la interpreta).</summary>
    public byte[] Data { get; set; } = [];

    /// <summary>Jugador que la controla en esta sesión (0 = nadie). No se guarda en disco.</summary>
    public int OwnerId { get; set; }

    public VesselUpdateMessage? LastUpdate { get; set; }

    public bool Dirty { get; set; }
}

/// <summary>Naves del universo: un archivo por nave en Universe/Vessels/{id}.vessel, con escritura atómica.</summary>
internal sealed class VesselStore
{
    /// <summary>1: sin versión de estructura. 2: con versión de estructura.</summary>
    private const int FileVersion = 2;
    private const string Extension = ".vessel";

    private readonly Dictionary<Guid, StoredVessel> _vessels = new();
    private readonly HashSet<Guid> _deleted = new();

    public VesselStore(string universeDirectory)
    {
        DirectoryPath = Path.Combine(universeDirectory, "Vessels");
    }

    public string DirectoryPath { get; }

    public int Count => _vessels.Count;

    public IEnumerable<StoredVessel> All => _vessels.Values;

    public StoredVessel? Get(Guid id) => _vessels.GetValueOrDefault(id);

    public StoredVessel GetOrCreate(Guid id)
    {
        if (!_vessels.TryGetValue(id, out StoredVessel? vessel))
        {
            vessel = new StoredVessel { Id = id, Dirty = true };
            _vessels[id] = vessel;
            _deleted.Remove(id);
        }

        return vessel;
    }

    public bool Remove(Guid id)
    {
        if (!_vessels.Remove(id))
            return false;

        _deleted.Add(id);
        return true;
    }

    public void Load()
    {
        _vessels.Clear();
        Directory.CreateDirectory(DirectoryPath);
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*" + Extension))
        {
            try
            {
                StoredVessel vessel = Deserialize(File.ReadAllBytes(file));
                _vessels[vessel.Id] = vessel;
            }
            catch (Exception ex) when (ex is IOException or ProtocolException)
            {
                Log.Warn($"No se pudo leer la nave {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public void SaveDirty()
    {
        Directory.CreateDirectory(DirectoryPath);
        foreach (StoredVessel vessel in _vessels.Values)
        {
            if (!vessel.Dirty)
                continue;

            string path = PathOf(vessel.Id);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, Serialize(vessel));
            File.Move(temp, path, overwrite: true);
            vessel.Dirty = false;
        }

        foreach (Guid id in _deleted)
            File.Delete(PathOf(id));
        _deleted.Clear();
    }

    internal static byte[] Serialize(StoredVessel vessel)
    {
        var writer = new PacketWriter(vessel.Data.Length + 256);
        writer.WriteInt32(FileVersion);
        writer.WriteGuid(vessel.Id);
        writer.WriteString(vessel.Name);
        writer.WriteInt32(vessel.StructureVersion);
        writer.WriteBytes(vessel.Data);
        writer.WriteBool(vessel.LastUpdate != null);
        vessel.LastUpdate?.Write(writer);
        return writer.ToArray();
    }

    internal static StoredVessel Deserialize(byte[] bytes)
    {
        var reader = new PacketReader(bytes);
        int version = reader.ReadInt32();
        if (version is < 1 or > FileVersion)
            throw new ProtocolException($"Versión de archivo desconocida: {version}");

        Guid id = reader.ReadGuid();
        string name = reader.ReadString() ?? string.Empty;
        int structureVersion = version >= 2 ? reader.ReadInt32() : 0;
        byte[] data = reader.ReadBytes() ?? [];
        if (id == Guid.Empty)
            throw new ProtocolException("Nave sin identificador");

        var vessel = new StoredVessel { Id = id, Name = name, StructureVersion = structureVersion, Data = data };
        if (reader.ReadBool())
        {
            var update = new VesselUpdateMessage();
            update.Read(reader);
            vessel.LastUpdate = update;
        }

        return vessel;
    }

    private string PathOf(Guid id) => Path.Combine(DirectoryPath, id.ToString("N") + Extension);
}
