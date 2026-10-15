using System.Text;
using MultiKerbal.Common;
using MultiKerbal.Relay;

Console.OutputEncoding = Encoding.UTF8;

var options = new RelayOptions
{
    Port = ProtocolInfo.DefaultRelayPort,
    ProbePort = ProtocolInfo.DefaultRelayPort + 1,
};

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int port):
            options.Port = port;
            i++;
            break;
        case "--probe-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int probePort):
            options.ProbePort = probePort;
            i++;
            break;
        case "--data" when i + 1 < args.Length:
            options.DataDirectory = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("Uso: MultiKerbal.Relay [--port 6760] [--probe-port 6761] [--data relay-data]");
            return 0;
        default:
            Console.Error.WriteLine($"Argumento desconocido: {args[i]}");
            return 1;
    }
}

RelayLog.OpenDaily("logs");

var host = new RelayHost(options);
try
{
    host.Start();
}
catch (InvalidDataException ex)
{
    RelayLog.Error($"No se arranca: {ex.Message}");
    RelayLog.Error($"Se detiene para no reasignar códigos que los jugadores ya conocen. Repara o restaura {Path.Combine(options.DataDirectory, "codes.json")} y vuelve a arrancar.");
    RelayLog.Close();
    return 1;
}
catch (Exception ex)
{
    RelayLog.Error($"No se pudo iniciar el relay: {ex.Message}");
    RelayLog.Close();
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
            host.ExecuteCommand(line);
    }
    catch (Exception)
    {
        // Sin consola interactiva (servicio, contenedor): el relay sigue funcionando.
    }
})
{
    IsBackground = true,
    Name = "MultiKerbal Relay Console",
};
input.Start();

RelayLog.Info("Escribe 'help' para ver los comandos.");
host.WaitForStopRequest();
host.Stop();
RelayLog.Info("Relay detenido");
RelayLog.Close();
return 0;
