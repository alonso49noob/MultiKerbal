using System.Text.Json;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Server.Systems;

/// <summary>Un kerbal tal como se guarda en Universe/kerbals.json.</summary>
internal sealed class KerbalData
{
    public string Name { get; set; } = string.Empty;

    public KerbalLife Life { get; set; }

    /// <summary>Nodo KERBAL de KSP en texto (rasgo, experiencia...).</summary>
    public string Node { get; set; } = string.Empty;
}

/// <summary>
/// Plantel compartido (en todos los modos): quién se contrató, quién murió o se perdió, quién volvió y su experiencia.
/// Cada jugador manda el kerbal que cambia en su partida; se guarda el último estado y se reenvía a los demás.
/// Que un kerbal esté o no a bordo de una nave lo dicen las naves, no esto.
/// </summary>
internal sealed class RosterSystem
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, KerbalData> _kerbals = new(StringComparer.OrdinalIgnoreCase);

    public RosterSystem(string universeDirectory)
    {
        _path = Path.Combine(universeDirectory, "kerbals.json");
    }

    public bool Dirty { get; private set; }

    public int Count => _kerbals.Count;

    public KerbalLife? LifeOf(string name) => _kerbals.TryGetValue(name, out KerbalData? kerbal) ? kerbal.Life : null;

    public void Load()
    {
        _kerbals.Clear();
        if (!File.Exists(_path))
            return;

        foreach (KerbalData kerbal in JsonSerializer.Deserialize<List<KerbalData>>(File.ReadAllText(_path), JsonOptions) ?? [])
        {
            if (IsValidName(kerbal.Name))
                _kerbals[kerbal.Name] = kerbal;
        }
    }

    public void Save()
    {
        if (!Dirty)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_kerbals.Values.OrderBy(k => k.Name).ToList(), JsonOptions));
        File.Move(temp, _path, overwrite: true);
        Dirty = false;
    }

    /// <summary>Guarda lo que manda un jugador. Devuelve los kerbals válidos (para reenviarlos a los demás).</summary>
    public List<KerbalInfo> Merge(IEnumerable<KerbalInfo> kerbals)
    {
        var accepted = new List<KerbalInfo>();
        foreach (KerbalInfo kerbal in kerbals)
        {
            string name = ServerHost.SanitizeText(kerbal.Name, 64);
            if (!IsValidName(name) || !Enum.IsDefined(kerbal.Life) || (kerbal.Node?.Length ?? 0) > ProtocolInfo.MaxNodeTextLength)
                continue;

            var info = new KerbalInfo { Name = name, Life = kerbal.Life, Node = kerbal.Node ?? string.Empty };
            Store(info);
            accepted.Add(info);
        }

        return accepted;
    }

    /// <summary>
    /// Un kerbal vuelve a estar vivo (la nave en la que murió se ha restaurado). Devuelve el cambio para reenviarlo,
    /// o null si no constaba como muerto o perdido.
    /// </summary>
    public KerbalInfo? Revive(string name)
    {
        if (!_kerbals.TryGetValue(name, out KerbalData? kerbal) || kerbal.Life is not (KerbalLife.Dead or KerbalLife.Missing))
            return null;

        var info = new KerbalInfo { Name = kerbal.Name, Life = KerbalLife.Alive, Node = kerbal.Node };
        Store(info);
        return info;
    }

    public KerbalsMessage BuildAll() => new()
    {
        Kerbals = _kerbals.Values.Select(k => new KerbalInfo { Name = k.Name, Life = k.Life, Node = k.Node }).ToArray(),
    };

    private void Store(KerbalInfo info)
    {
        _kerbals.TryGetValue(info.Name, out KerbalData? known);
        _kerbals[info.Name] = new KerbalData
        {
            Name = info.Name,
            Life = info.Life,
            // Un aviso sin nodo (solo cambia el estado) conserva la definición que ya había.
            Node = info.Node.Length > 0 ? info.Node : known?.Node ?? string.Empty,
        };
        Dirty = true;
    }

    private static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64;
}
