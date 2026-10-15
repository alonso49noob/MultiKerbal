using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;

namespace MultiKerbal.Relay;

public sealed partial class RelayHost
{
    /// <summary>Ejecuta una línea de la consola: <c>list</c>, <c>ban &lt;código&gt;</c>, <c>unban &lt;código&gt;</c> o <c>stop</c>.</summary>
    public void ExecuteCommand(string line)
    {
        string trimmed = (line ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return;

        int space = trimmed.IndexOf(' ');
        string command = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        string arguments = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        switch (command)
        {
            case "help":
            case "ayuda":
                RelayLog.Info("Comandos: list | ban <código> | unban <código> | stop");
                break;

            case "list":
                ListServers();
                break;

            case "ban":
                BanCommand(arguments, ban: true);
                break;

            case "unban":
                BanCommand(arguments, ban: false);
                break;

            case "stop":
            case "quit":
            case "exit":
                RequestStop();
                break;

            default:
                RelayLog.Info($"Comando desconocido: {command}. Escribe 'help'.");
                break;
        }
    }

    private void ListServers()
    {
        ServerSession[] servers;
        lock (_registryLock)
            servers = _servers.Values.OrderBy(s => s.Code, StringComparer.Ordinal).ToArray();

        RelayLog.Info($"{servers.Length} servidor(es) vivo(s):");
        foreach (ServerSession server in servers)
        {
            RelayServerInfo info = server.Info;
            TimeSpan age = DateTime.UtcNow - server.RegisteredAt;
            RelayLog.Info($"  {ServerCode.Format(server.Code)} \"{ListedName.Sanitize(info.Name)}\" {info.Players}/{info.MaxPlayers} — {server.RemoteAddress}:{server.GamePort}, " +
                          $"{(info.IsPublic ? "público" : "privado")}, registrado hace {(int)age.TotalMinutes} min");
        }
    }

    /// <summary>
    /// <c>ban</c> lo saca de la lista, cierra su conexión y bloquea su clave (el código queda guardado como baneado);
    /// <c>unban</c> lo permite otra vez.
    /// </summary>
    private void BanCommand(string arguments, bool ban)
    {
        string verb = ban ? "ban" : "unban";
        if (!ServerCode.TryNormalize(arguments, out string code))
        {
            RelayLog.Info(arguments.Length == 0 ? $"Uso: {verb} <código>" : $"\"{arguments}\" no es un código válido");
            return;
        }

        ServerSession? live = null;
        bool known;
        try
        {
            // Dentro del cerrojo del registro, para que un registro que llegue a la vez no se cuele.
            lock (_registryLock)
            {
                known = ban ? _codes!.Ban(code) : _codes!.Unban(code);

                // Se saca del registro ya, no cuando termine su lectura: así la consulta no lo encuentra ni un instante.
                if (ban && known)
                    _servers.Remove(code, out live);
            }
        }
        catch (IOException ex)
        {
            RelayLog.Error($"No se pudo guardar el cambio en codes.json: {ex.Message}");
            return;
        }

        if (!known)
        {
            RelayLog.Info($"No existe ningún servidor con el código {ServerCode.Format(code)}");
            return;
        }

        live?.Close();
        RelayLog.Info(ban
            ? $"Código {ServerCode.Format(code)} baneado{(live != null ? " y desconectado" : string.Empty)}"
            : $"Código {ServerCode.Format(code)} permitido otra vez");
    }
}
