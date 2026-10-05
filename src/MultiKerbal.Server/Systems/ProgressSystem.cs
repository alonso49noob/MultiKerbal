using System.Text.Json;
using System.Text.RegularExpressions;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Progress;

namespace MultiKerbal.Server.Systems;

/// <summary>Progreso compartido tal como se guarda en Universe/progress.json.</summary>
internal sealed class ProgressData
{
    public double Funds { get; set; }

    public double Science { get; set; }

    public double Reputation { get; set; }

    public List<string> Techs { get; set; } = [];
}

/// <summary>
/// Progreso compartido por todos los jugadores (ciencia y tecnologías en modo ciencia; además fondos y reputación
/// en carrera). Cada cliente manda lo que cambia en su partida como diferencia y el servidor la suma: así dos
/// jugadores que gastan a la vez no se pisan. Después se manda el total a todos.
/// </summary>
internal sealed partial class ProgressSystem
{
    /// <summary>Un solo cambio no puede mover más que esto (protege de valores absurdos).</summary>
    internal const double MaxChange = 1e9;

    /// <summary>El nodo raíz del árbol tecnológico de KSP: siempre investigado.</summary>
    internal const string StartTech = "start";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private ProgressData _data = new();
    private readonly HashSet<string> _techs = new(StringComparer.Ordinal);

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
            if (IsValidTechId(tech))
                _techs.Add(tech);
        }
    }

    public void Save()
    {
        if (!Dirty)
            return;

        _data.Techs = _techs.OrderBy(t => t, StringComparer.Ordinal).ToList();
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
                if (!IsValidTechId(change.TechId) || !_techs.Add(change.TechId!))
                    return false;
                Dirty = true;
                return true;
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
    };

    internal static bool IsValidTechId(string? id) => id != null && id.Length is > 0 and <= 64 && TechIdRegex().IsMatch(id);

    private bool Add(double amount, Action<double> apply)
    {
        if (!double.IsFinite(amount) || amount == 0 || Math.Abs(amount) > MaxChange)
            return false;

        apply(amount);
        Dirty = true;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex TechIdRegex();
}
