using MultiKerbal.Common;
using MultiKerbal.Common.Time;

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
                Log.Info("Comandos: list | vessels | warp | say <texto> | kick <jugador> [motivo] | time | save | stop");
                break;

            case "vessels":
            case "naves":
                ListVessels();
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
