using System.Text.Encodings.Web;
using System.Text.Json;
using MultiKerbal.Common;

namespace MultiKerbal.Server;

public sealed class ServerConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ServerName { get; set; } = "Servidor MultiKerbal";

    public int Port { get; set; } = ProtocolInfo.DefaultPort;

    public int MaxPlayers { get; set; } = 8;

    /// <summary>Vacío = sin contraseña.</summary>
    public string Password { get; set; } = "";

    public string Motd { get; set; } = "¡Bienvenido a MultiKerbal!";

    /// <summary>Detiene el reloj universal mientras no haya nadie conectado.</summary>
    public bool PauseClockWhenEmpty { get; set; } = true;

    public string DataDirectory { get; set; } = "Universe";

    public static ServerConfig LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            ServerConfig config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOptions) ?? new ServerConfig();
            config.Validate();
            return config;
        }

        var created = new ServerConfig();
        File.WriteAllText(path, JsonSerializer.Serialize(created, JsonOptions));
        Log.Info($"Creado archivo de configuración por defecto: {Path.GetFullPath(path)}");
        return created;
    }

    public void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new InvalidDataException($"Puerto inválido: {Port}");

        MaxPlayers = Math.Clamp(MaxPlayers, 1, ProtocolInfo.MaxPlayers);
        ServerName = string.IsNullOrWhiteSpace(ServerName) ? "Servidor MultiKerbal" : ServerName.Trim();
        Password ??= "";
        Motd ??= "";
        if (string.IsNullOrWhiteSpace(DataDirectory))
            DataDirectory = "Universe";
    }
}
