using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Serialization;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Server.Persistence;

internal sealed class StoredVessel
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Versión de estructura enviada por quien la pilota (ver <see cref="VesselProtoMessage.StructureVersion"/>).</summary>
    public int StructureVersion { get; set; }

    /// <summary>Definición comprimida tal como la envió el cliente (el servidor no la interpreta).</summary>
    public byte[] Data { get; set; } = [];

    /// <summary>Nombre del jugador dueño (vacío = sin dueño). Se guarda en disco.</summary>
    public string OwnerName { get; set; } = string.Empty;

    public VesselAccess Access { get; set; } = VesselAccess.Shared;

    /// <summary>Jugador al que el dueño se la ha prestado (vacío = a nadie). Se guarda en disco.</summary>
    public string LentTo { get; set; } = string.Empty;

    /// <summary>
    /// Jugador que la publicó. Suele ser el dueño; no lo es en las etapas que suelta quien vuela una nave ajena
    /// (siguen siendo del dueño), y ese jugador sí puede perderlas (se queman, se estrellan).
    /// </summary>
    public string Creator { get; set; } = string.Empty;

    /// <summary>Kerbals a bordo según quien la pilota: un kerbal no puede ir en naves de dos jugadores.</summary>
    public string[] Crew { get; set; } = [];

    /// <summary>
    /// Cómo estaba cuando la tomó alguien sin permiso para destruirla: si la destruye, vuelve aquí.
    /// Null si quien la pilota puede destruirla.
    /// </summary>
    public VesselCheckpoint? Checkpoint { get; set; }

    /// <summary>Jugador que la pilota en esta sesión (0 = nadie). No se guarda en disco.</summary>
    public int ControllerId { get; set; }

    /// <summary>Copiloto: sus mandos se reenvían a quien la pilota (0 = ninguno). No se guarda en disco.</summary>
    public int CopilotId { get; set; }

    /// <summary>Si el copiloto puede además accionar etapas y grupos de acción.</summary>
    public bool CopilotActions { get; set; }

    public VesselUpdateMessage? LastUpdate { get; set; }

    public bool Dirty { get; set; }
}

/// <summary>Definición y estado de una nave en un momento dado (ver <see cref="StoredVessel.Checkpoint"/>).</summary>
internal sealed record VesselCheckpoint(byte[] Data, string[] Crew, VesselUpdateMessage? LastUpdate);

/// <summary>Naves del universo: un archivo por nave en Universe/Vessels/{id}.vessel, con escritura atómica.</summary>
internal sealed class VesselStore
{
    /// <summary>
    /// 1: sin versión de estructura. 2: con versión de estructura. 3: con dueño y acceso.
    /// 4: con préstamo, tripulación y punto de restauración. 5: con quién la creó.
    /// </summary>
    private const int FileVersion = 5;
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
        writer.WriteString(vessel.OwnerName);
        writer.WriteByte((byte)vessel.Access);
        writer.WriteString(vessel.LentTo);
        writer.WriteStrings(vessel.Crew);
        writer.WriteString(vessel.Creator);
        writer.WriteBytes(vessel.Data);
        WriteUpdate(writer, vessel.LastUpdate);
        writer.WriteBool(vessel.Checkpoint != null);
        if (vessel.Checkpoint != null)
        {
            writer.WriteBytes(vessel.Checkpoint.Data);
            writer.WriteStrings(vessel.Checkpoint.Crew);
            WriteUpdate(writer, vessel.Checkpoint.LastUpdate);
        }

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
        // Las naves de versiones anteriores no tienen dueño: cualquiera puede reclamarlas.
        string owner = version >= 3 ? reader.ReadString() ?? string.Empty : string.Empty;
        var access = version >= 3 ? (VesselAccess)reader.ReadByte() : VesselAccess.Shared;
        string lentTo = version >= 4 ? reader.ReadString() ?? string.Empty : string.Empty;
        string[] crew = version >= 4 ? reader.ReadStrings(ProtocolInfo.MaxCrewPerVessel) : [];
        // Antes se creaba siempre a nombre del dueño.
        string creator = version >= 5 ? reader.ReadString() ?? string.Empty : owner;
        byte[] data = reader.ReadBytes() ?? [];
        if (id == Guid.Empty)
            throw new ProtocolException("Nave sin identificador");
        if (!VesselPermissions.IsValid(access))
            access = VesselAccess.Shared;

        var vessel = new StoredVessel
        {
            Id = id,
            Name = name,
            StructureVersion = structureVersion,
            OwnerName = owner,
            Access = access,
            LentTo = lentTo,
            Crew = crew,
            Creator = creator,
            Data = data,
            LastUpdate = ReadUpdate(reader),
        };
        if (version >= 4 && reader.ReadBool())
        {
            byte[] checkpointData = reader.ReadBytes() ?? [];
            string[] checkpointCrew = reader.ReadStrings(ProtocolInfo.MaxCrewPerVessel);
            vessel.Checkpoint = new VesselCheckpoint(checkpointData, checkpointCrew, ReadUpdate(reader));
        }

        return vessel;
    }

    private static void WriteUpdate(PacketWriter writer, VesselUpdateMessage? update)
    {
        writer.WriteBool(update != null);
        update?.Write(writer);
    }

    private static VesselUpdateMessage? ReadUpdate(PacketReader reader)
    {
        if (!reader.ReadBool())
            return null;

        var update = new VesselUpdateMessage();
        update.Read(reader);
        return update;
    }

    private string PathOf(Guid id) => Path.Combine(DirectoryPath, id.ToString("N") + Extension);
}
