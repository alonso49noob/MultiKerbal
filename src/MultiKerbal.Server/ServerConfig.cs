using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiKerbal.Common;
using MultiKerbal.Common.Progress;
using MultiKerbal.Common.Relay;

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

    /// <summary>
    /// Qué hacer con quien no tenga los mismos mods que el servidor:
    /// <c>off</c> ni se mira, <c>warn</c> se avisa y se entra igual, <c>strict</c> se rechaza la conexión.
    /// </summary>
    public string ModPolicy { get; set; } = "warn";

    /// <summary>Idioma de los mensajes que ve el jugador (chat y motivos de rechazo): "es" o "en".</summary>
    public string Language { get; set; } = "es";

    /// <summary>
    /// Tipo de partida: <c>sandbox</c> (sin progreso), <c>science</c> (ciencia y tecnologías compartidas) o
    /// <c>career</c> (además fondos y reputación). Cambiarlo con jugadores dentro no les afecta hasta que vuelvan a entrar.
    /// </summary>
    public string GameMode { get; set; } = "sandbox";

    /// <summary>Fondos con los que empieza una carrera nueva.</summary>
    public double StartingFunds { get; set; } = 25_000;

    /// <summary>Registrarse en el relay: da un código para entrar sin abrir puertos y permite aparecer en la lista pública.</summary>
    public bool Relay { get; set; } = true;

    /// <summary>Dirección del relay, "host" o "host:puerto".</summary>
    public string RelayAddress { get; set; } = ProtocolInfo.DefaultRelayAddress;

    /// <summary>Aparecer en la lista pública de servidores del relay (con <see cref="Relay"/>). Por defecto no: solo se entra con el código.</summary>
    public bool Public { get; set; } = false;

    [JsonIgnore]
    public GameMode Mode { get; private set; } = Common.Progress.GameMode.Sandbox;

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
        Language = (Language ?? "es").Trim().ToLowerInvariant() == "en" ? "en" : "es";
        Lang.English = Language == "en";
        ModPolicy = (ModPolicy ?? "warn").Trim().ToLowerInvariant();
        if (ModPolicy is not ("off" or "warn" or "strict"))
        {
            Log.Warn($"ModPolicy \"{ModPolicy}\" no es válido (off, warn o strict): se usa warn");
            ModPolicy = "warn";
        }

        GameMode = (GameMode ?? "sandbox").Trim().ToLowerInvariant();
        switch (GameMode)
        {
            case "science":
            case "ciencia":
                Mode = Common.Progress.GameMode.Science;
                break;
            case "career":
            case "carrera":
                Mode = Common.Progress.GameMode.Career;
                break;
            default:
                if (GameMode != "sandbox")
                    Log.Warn($"GameMode \"{GameMode}\" no es válido (sandbox, science o career): se usa sandbox");
                GameMode = "sandbox";
                Mode = Common.Progress.GameMode.Sandbox;
                break;
        }

        if (!double.IsFinite(StartingFunds) || StartingFunds < 0)
            StartingFunds = 25_000;

        if (Relay && !RelayEndpoint.TryParse(RelayAddress ?? string.Empty, ProtocolInfo.DefaultRelayPort, out _, out _))
        {
            Log.Warn($"RelayAddress \"{RelayAddress}\" no es una dirección válida (host o host:puerto): se desactiva el relay");
            Relay = false;
        }

        ServerName = string.IsNullOrWhiteSpace(ServerName) ? "Servidor MultiKerbal" : ServerName.Trim();
        Password ??= "";
        Motd ??= "";
        if (string.IsNullOrWhiteSpace(DataDirectory))
            DataDirectory = "Universe";
    }
}
