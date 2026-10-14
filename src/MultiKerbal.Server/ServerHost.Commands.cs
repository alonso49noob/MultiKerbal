using MultiKerbal.Common;
using MultiKerbal.Common.Mods;
using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Time;
using MultiKerbal.Server.Persistence;

namespace MultiKerbal.Server;

public sealed partial class ServerHost
{
    private void ExecuteCommand(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return;

        int space = trimmed.IndexOf(' ');
        string command = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        string arguments = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        switch (command)
        {
            case "help":
            case "ayuda":
                Log.Info("Comandos: list | vessels | owner <nave> <jugador|nadie> [privada|compartida|publica] | account [delete <jugador>] | progress | relay | mods [jugador|set <jugador>] | warp | say <texto> | kick <jugador> [motivo] | time | save | stop");
                break;

            case "vessels":
            case "naves":
                ListVessels();
                break;

            case "owner":
            case "dueño":
                SetOwnerCommand(arguments);
                break;

            case "mods":
                ModsCommand(arguments);
                break;

            case "warp":
                ListWarpVotes();
                break;

            case "list":
            case "players":
                List<Player> players = AuthenticatedPlayers().ToList();
                Log.Info(players.Count == 0
                    ? "No hay jugadores conectados"
                    : $"{players.Count} jugador(es): {string.Join(", ", players.Select(p => $"{p.Name} [{p.Info.Activity}]"))}");
                break;

            case "say":
                string text = SanitizeText(arguments, ProtocolInfo.MaxChatLength);
                if (text.Length == 0)
                {
                    Log.Info("Uso: say <texto>");
                    break;
                }

                SendSystemChat(text);
                Log.Chat($"[Servidor] {text}");
                break;

            case "kick":
                Kick(arguments);
                break;

            case "time":
            case "ut":
                double now = _clock.Now;
                double ut = _time.CurrentUniversalTime(now);
                string state = _time.Paused ? "pausado" : $"warp x{_time.Rate:0.##}";
                Log.Info($"{KerbalTime.Format(ut)} (UT {ut:0.0}) — {state}");
                break;

            case "save":
                SaveUniverse();
                Log.Info("Universo guardado");
                break;

            case "account":
            case "accounts":
            case "cuenta":
            case "cuentas":
                AccountCommand(arguments);
                break;

            case "progress":
            case "progreso":
                ShowProgress();
                break;

            case "relay":
                Log.Info(DescribeRelay());
                break;

            case "stop":
            case "quit":
            case "exit":
                _running = false;
                break;

            default:
                Log.Info($"Comando desconocido: {command}. Escribe 'help'.");
                break;
        }
    }

    /// <summary>
    /// <c>mods</c> lista los mods que espera el servidor, <c>mods &lt;jugador&gt;</c> compara con los suyos y
    /// <c>mods set &lt;jugador&gt;</c> adopta los de ese jugador como los del servidor.
    /// </summary>
    private void ModsCommand(string arguments)
    {
        bool adopt = arguments.StartsWith("set ", StringComparison.OrdinalIgnoreCase);
        string playerName = adopt ? arguments[4..].Trim() : arguments.Trim();

        if (playerName.Length == 0)
        {
            Log.Info(_mods.HasReference
                ? $"{_mods.Mods.Length} mod(s) esperados (de {_mods.Source}), control \"{Config.ModPolicy}\":"
                : $"Sin lista de mods: la fijará el primer jugador (control \"{Config.ModPolicy}\")");
            foreach (ModInfo mod in _mods.Mods.OrderBy(m => m.Name))
                Log.Info($"  {mod.Name}{(mod.Version.Length > 0 ? " " + mod.Version : string.Empty)}");
            return;
        }

        Player? target = FindPlayer(playerName);
        if (target == null)
        {
            Log.Info($"No hay ningún jugador llamado \"{playerName}\"");
            return;
        }

        if (adopt)
        {
            _mods.Set(target.Mods, target.Name);
            Log.Info($"Lista de mods actualizada con la de {target.Name}: {_mods.Mods.Length} mod(s)");
            return;
        }

        List<ModDifference> differences = ModCompare.Compare(_mods.Mods, target.Mods);
        Log.Info($"{target.Name}: {ModCompare.Summarize(differences)}");
        foreach (ModDifference difference in differences.Where(d => d.IsProblem))
            Log.Info($"  {Describe(difference)}");
    }

    private static string Describe(ModDifference difference)
    {
        switch (difference.Status)
        {
            case ModStatus.Missing:
                return $"falta {difference.Name} {difference.ServerVersion}".TrimEnd();
            case ModStatus.Extra:
                return $"sobra {difference.Name} {difference.PlayerVersion}".TrimEnd();
            case ModStatus.OtherVersion:
                return $"{difference.Name}: servidor {difference.ServerVersion}, jugador {difference.PlayerVersion}";
            default:
                return difference.Name;
        }
    }

    /// <summary>
    /// <c>account</c> lista las cuentas; <c>account delete &lt;jugador&gt;</c> borra una (por ejemplo, si olvidó la
    /// contraseña): la próxima vez que entre con ese nombre, la contraseña que use será la nueva.
    /// </summary>
    private void AccountCommand(string arguments)
    {
        string[] words = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            Log.Info($"{_accounts.Count} cuenta(s):");
            foreach (Account account in _accounts.All.OrderBy(a => a.Name))
                Log.Info($"  {account.Name} — creada {account.Created:yyyy-MM-dd}, última entrada {account.LastLogin:yyyy-MM-dd HH:mm} UTC");
            return;
        }

        if (words.Length < 2 || words[0].ToLowerInvariant() is not ("delete" or "borrar"))
        {
            Log.Info("Uso: account | account delete <jugador>");
            return;
        }

        string name = words[1].Trim();
        if (FindPlayer(name) is { } connected)
        {
            Log.Info($"{connected.Name} está conectado: expúlsalo antes de borrar su cuenta");
            return;
        }

        Log.Info(_accounts.Delete(name)
            ? $"Cuenta de {name} borrada: la próxima vez que entre elegirá contraseña nueva"
            : $"No hay ninguna cuenta llamada \"{name}\"");
    }

    private void ShowProgress()
    {
        Log.Info($"Plantel compartido: {_roster.Count} kerbal(s) con historia");
        if (_progress.Mode == GameMode.Sandbox)
        {
            Log.Info("Partida sandbox: no hay más progreso compartido (GameMode en server.json)");
            return;
        }

        Log.Info($"Partida {GameModes.Describe(_progress.Mode)}: fondos {_progress.Funds:N0}, ciencia {_progress.Science:0.#}, reputación {_progress.Reputation:0.#}");
        Log.Info($"  {_progress.Techs.Count} tecnología(s), {_progress.SubjectCount} experimento(s) con ciencia, {_progress.Parts.Count} pieza(s) compradas, {_progress.ActiveContracts} contrato(s) activos");
    }

    private Player? FindPlayer(string name) =>
        AuthenticatedPlayers().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Los nombres pueden tener espacios: se busca el jugador cuyo nombre sea prefijo de los argumentos.</summary>
    private void Kick(string arguments)
    {
        Player? target = AuthenticatedPlayers()
            .Where(p => arguments.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase)
                        && (arguments.Length == p.Name.Length || arguments[p.Name.Length] == ' '))
            .MaxBy(p => p.Name.Length);

        if (target == null)
        {
            Log.Info(arguments.Length == 0 ? "Uso: kick <jugador> [motivo]" : $"No hay ningún jugador llamado \"{arguments}\"");
            return;
        }

        string reason = arguments[target.Name.Length..].Trim();
        _network.Kick(target.Connection, reason.Length > 0 ? $"Expulsado: {reason}" : "Expulsado por el administrador");
        Log.Info($"{target.Name} expulsado");
    }
}
