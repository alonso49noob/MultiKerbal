using System.Text;
using MultiKerbal.Server;

Console.OutputEncoding = Encoding.UTF8;

string configPath = "server.json";
int? portOverride = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int port):
            portOverride = port;
            i++;
            break;
        case "--help" or "-h":
            Console.WriteLine("Uso: MultiKerbal.Server [--config server.json] [--port 6750]");
            return 0;
        default:
            Console.Error.WriteLine($"Argumento desconocido: {args[i]}");
            return 1;
    }
}

Log.OpenFile(Path.Combine("logs", $"server-{DateTime.Now:yyyyMMdd}.log"));

ServerConfig config;
try
{
    config = ServerConfig.LoadOrCreate(configPath);
    if (portOverride.HasValue)
        config.Port = portOverride.Value;
}
catch (Exception ex)
{
    Log.Error($"No se pudo leer la configuración {configPath}: {ex.Message}");
    return 1;
}

var host = new ServerHost(config);
try
{
    host.Start();
}
catch (Exception ex)
{
    Log.Error($"No se pudo iniciar el servidor: {ex.Message}");
    return 1;
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    host.RequestStop();
};

var input = new Thread(() =>
{
    try
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
            host.EnqueueCommand(line);
    }
    catch (Exception)
    {
        // Sin consola interactiva (servicio, contenedor): el servidor sigue funcionando.
    }
})
{
    IsBackground = true,
    Name = "MultiKerbal Console",
};
input.Start();

Log.Info("Escribe 'help' para ver los comandos.");
host.WaitForExit();
Log.Close();
return 0;
