using System.Text.Json;
using System.Text.RegularExpressions;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Progress;

namespace MultiKerbal.Server.Systems;

/// <summary>Un experimento con ciencia conseguida, tal como se guarda.</summary>
internal sealed class SubjectData
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public float DataScale { get; set; }

    public float SubjectValue { get; set; }

    public float ScienceCap { get; set; }

    public float Science { get; set; }

    public float ScientificValue { get; set; }
}

/// <summary>Un contrato aceptado o terminado, tal como se guarda.</summary>
internal sealed class ContractData
{
    public Guid Id { get; set; }

    public ContractStatus Status { get; set; }

    public string Node { get; set; } = string.Empty;
}

/// <summary>Progreso compartido tal como se guarda en Universe/progress.json.</summary>
internal sealed class ProgressData
{
    public double Funds { get; set; }

    public double Science { get; set; }

    public double Reputation { get; set; }

    public List<string> Techs { get; set; } = [];

    public List<string> Parts { get; set; } = [];

    public Dictionary<string, float> Facilities { get; set; } = [];

    public List<SubjectData> Subjects { get; set; } = [];

    public List<ContractData> Contracts { get; set; } = [];
}

/// <summary>
/// Progreso compartido por todos los jugadores (ciencia, tecnologías y experimentos en modo ciencia; además fondos,
/// reputación, piezas compradas, edificios y contratos en carrera).
/// <list type="bullet">
/// <item>Fondos, ciencia y reputación llegan como diferencias y se suman: dos jugadores que gastan a la vez no se pisan.</item>
/// <item>Lo demás solo avanza (tecnologías, piezas, nivel de edificios, ciencia de cada experimento): se queda lo mayor.</item>
/// <item>Un contrato aceptado es de todos; el primero que lo termina decide cómo acaba.</item>
/// </list>
/// </summary>
internal sealed partial class ProgressSystem
{
    /// <summary>Un solo cambio no puede mover más que esto (protege de valores absurdos).</summary>
    internal const double MaxChange = 1e9;

    /// <summary>El nodo raíz del árbol tecnológico de KSP: siempre investigado.</summary>
    internal const string StartTech = "start";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly HashSet<string> _techs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _facilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubjectData> _subjects = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ContractData> _contracts = new();
    private ProgressData _data = new();

    public ProgressSystem(string universeDirectory, GameMode mode, double startingFunds)
    {
        _path = Path.Combine(universeDirectory, "progress.json");
        Mode = mode;
        StartingFunds = startingFunds;
    }

    public GameMode Mode { get; }

    public double StartingFunds { get; }

    public bool Dirty { get; private set; }

    public double Funds => _data.Funds;

    public double Science => _data.Science;

    public double Reputation => _data.Reputation;

    public IReadOnlyCollection<string> Techs => _techs;

    public IReadOnlyCollection<string> Parts => _parts;

    public int SubjectCount => _subjects.Count;

    public int ActiveContracts => _contracts.Values.Count(c => c.Status == ContractStatus.Active);

    public void Load()
    {
        if (File.Exists(_path))
        {
            _data = JsonSerializer.Deserialize<ProgressData>(File.ReadAllText(_path), JsonOptions) ?? new ProgressData();
        }
        else
        {
            // Partida nueva: los fondos iniciales de la carrera; ciencia y reputación, a cero.
            _data = new ProgressData { Funds = Mode == GameMode.Career ? StartingFunds : 0 };
            Dirty = true;
        }

        _techs.Clear();
        _techs.Add(StartTech);
        foreach (string tech in _data.Techs ?? [])
        {
            if (IsValidId(tech))
                _techs.Add(tech);
        }

        _parts.Clear();
        foreach (string part in _data.Parts ?? [])
        {
            if (IsValidId(part))
                _parts.Add(part);
        }

        _facilities.Clear();
        foreach (KeyValuePair<string, float> facility in _data.Facilities ?? [])
        {
            if (IsValidFacilityId(facility.Key) && IsValidLevel(facility.Value))
                _facilities[facility.Key] = facility.Value;
        }

        _subjects.Clear();
        foreach (SubjectData subject in _data.Subjects ?? [])
        {
            if (IsValidSubjectId(subject.Id))
                _subjects[subject.Id] = subject;
        }

        _contracts.Clear();
        foreach (ContractData contract in _data.Contracts ?? [])
        {
            if (contract.Id != Guid.Empty && contract.Node.Length > 0)
                _contracts[contract.Id] = contract;
        }
    }

    public void Save()
    {
        if (!Dirty)
            return;

        _data.Techs = _techs.OrderBy(t => t, StringComparer.Ordinal).ToList();
        _data.Parts = _parts.OrderBy(p => p, StringComparer.Ordinal).ToList();
        _data.Facilities = new Dictionary<string, float>(_facilities);
        _data.Subjects = _subjects.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        _data.Contracts = _contracts.Values.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
        File.Move(temp, _path, overwrite: true);
        Dirty = false;
    }

    /// <summary>Aplica un cambio de un jugador. Devuelve false si no cambia nada (otro modo, valor inválido...).</summary>
    public bool Apply(ProgressChangeMessage change)
    {
        if (!GameModes.Applies(Mode, change.Kind))
            return false;

        switch (change.Kind)
        {
            case ProgressKind.Funds:
                return Add(change.Amount, amount => _data.Funds += amount);
            case ProgressKind.Science:
                return Add(change.Amount, amount => _data.Science += amount);
            case ProgressKind.Reputation:
                return Add(change.Amount, amount => _data.Reputation += amount);
            case ProgressKind.Tech:
                return AddTo(_techs, change.Id);
            case ProgressKind.Part:
                return AddTo(_parts, change.Id);
            case ProgressKind.Facility:
                return RaiseFacility(change.Id, change.Amount);
            default:
                return false;
        }
    }

    public ProgressStateMessage BuildState() => new()
    {
        Funds = _data.Funds,
        Science = _data.Science,
        Reputation = _data.Reputation,
        Techs = _techs.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
        Parts = _parts.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        Facilities = _facilities.Select(f => new FacilityLevel { Id = f.Key, Level = f.Value }).ToArray(),
    };

    /// <summary>
    /// Se queda la versión de cada experimento con más ciencia conseguida. Devuelve los que han cambiado (para
    /// reenviarlos a los demás).
    /// </summary>
    public List<ScienceSubjectInfo> MergeSubjects(IEnumerable<ScienceSubjectInfo> subjects)
    {
        var changed = new List<ScienceSubjectInfo>();
        if (!GameModes.SharesScience(Mode))
            return changed;

        foreach (ScienceSubjectInfo subject in subjects)
        {
            if (!IsValidSubject(subject))
                continue;
            if (_subjects.TryGetValue(subject.Id, out SubjectData? known) && known.Science >= subject.Science)
                continue;

            _subjects[subject.Id] = new SubjectData
            {
                Id = subject.Id,
                Title = ServerHost.SanitizeText(subject.Title, 200),
                DataScale = subject.DataScale,
                SubjectValue = subject.SubjectValue,
                ScienceCap = subject.ScienceCap,
                Science = subject.Science,
                ScientificValue = Math.Clamp(subject.ScientificValue, 0f, 1f),
            };
            changed.Add(subject);
            Dirty = true;
        }

        return changed;
    }

    public ScienceSubjectsMessage BuildSubjects() => new()
    {
        Subjects = _subjects.Values.Select(s => new ScienceSubjectInfo
        {
            Id = s.Id,
            Title = s.Title,
            DataScale = s.DataScale,
            SubjectValue = s.SubjectValue,
            ScienceCap = s.ScienceCap,
            Science = s.Science,
            ScientificValue = s.ScientificValue,
        }).ToArray(),
    };

    /// <summary>
    /// Un contrato aceptado o terminado por un jugador. Devuelve null si se acepta tal cual (hay que reenviarlo a los
    /// demás), o la versión del servidor si ya estaba terminado (hay que devolvérsela a quien lo mandó).
    /// </summary>
    public ContractUpdateResult MergeContract(ContractInfo contract)
    {
        if (!GameModes.SharesMoney(Mode) || contract.Id == Guid.Empty || !Enum.IsDefined(contract.Status)
            || string.IsNullOrEmpty(contract.Node) || contract.Node.Length > ProtocolInfo.MaxNodeTextLength)
            return ContractUpdateResult.Ignored;

        _contracts.TryGetValue(contract.Id, out ContractData? known);
        if (known != null && known.Status != ContractStatus.Active)
            return contract.Status == known.Status ? ContractUpdateResult.Ignored : ContractUpdateResult.AlreadyFinished;
        if (known != null && contract.Status == ContractStatus.Active)
            return ContractUpdateResult.Ignored;

        _contracts[contract.Id] = new ContractData { Id = contract.Id, Status = contract.Status, Node = contract.Node };
        Dirty = true;
        return ContractUpdateResult.Accepted;
    }

    public ContractInfo? GetContract(Guid id) =>
        _contracts.TryGetValue(id, out ContractData? data)
            ? new ContractInfo { Id = data.Id, Status = data.Status, Node = data.Node }
            : null;

    public ContractsMessage BuildContracts() => new()
    {
        Contracts = _contracts.Values.Select(c => new ContractInfo { Id = c.Id, Status = c.Status, Node = c.Node }).ToArray(),
    };

    internal static bool IsValidId(string? id) => id != null && id.Length is > 0 and <= 128 && IdRegex().IsMatch(id);

    /// <summary>Los identificadores de edificio los forma KSP (con barras u otros separadores): solo se limitan.</summary>
    internal static bool IsValidFacilityId(string? id) => id != null && id.Length is > 0 and <= 128 && !id.Any(char.IsControl);

    private static bool IsValidSubjectId(string? id) => id != null && id.Length is > 0 and <= 256 && !id.Any(char.IsControl);

    private static bool IsValidLevel(double level) => double.IsFinite(level) && level >= 0 && level <= 1;

    private static bool IsValidSubject(ScienceSubjectInfo subject) =>
        IsValidSubjectId(subject.Id)
        && float.IsFinite(subject.DataScale)
        && float.IsFinite(subject.SubjectValue)
        && float.IsFinite(subject.ScienceCap) && subject.ScienceCap >= 0
        && float.IsFinite(subject.Science) && subject.Science >= 0 && subject.Science <= Math.Max(subject.ScienceCap, 0) + 1
        && float.IsFinite(subject.ScientificValue);

    private bool Add(double amount, Action<double> apply)
    {
        if (!double.IsFinite(amount) || amount == 0 || Math.Abs(amount) > MaxChange)
            return false;

        apply(amount);
        Dirty = true;
        return true;
    }

    private bool AddTo(HashSet<string> set, string? id)
    {
        if (!IsValidId(id) || !set.Add(id!))
            return false;

        Dirty = true;
        return true;
    }

    /// <summary>Los edificios solo se mejoran: se queda el nivel más alto.</summary>
    private bool RaiseFacility(string? id, double level)
    {
        if (!IsValidFacilityId(id) || !IsValidLevel(level))
            return false;
        if (_facilities.TryGetValue(id!, out float known) && known >= level)
            return false;

        _facilities[id!] = (float)level;
        Dirty = true;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex IdRegex();

}

internal enum ContractUpdateResult
{
    /// <summary>Nuevo o terminado ahora: se reenvía a los demás.</summary>
    Accepted,

    /// <summary>Ya estaba terminado de otra forma: se devuelve la versión del servidor a quien lo mandó.</summary>
    AlreadyFinished,

    Ignored,
}
