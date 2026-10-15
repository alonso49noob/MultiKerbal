using System.Text;
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

    /// <summary>
    /// Solo para los tests: puerto de juego que se anuncia al relay en lugar del real. Apuntándolo a un puerto
    /// cerrado, la comprobación de acceso falla y los jugadores entran por el relay aunque estén en el mismo equipo.
    /// </summary>
    [JsonIgnore]
    internal int? RelayAnnouncedPortOverride { get; set; }

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

    /// <summary>
    /// Cambia SOLO la clave <c>Public</c> del server.json que ya existe, sin tocar nada más (ni los comentarios, ni el
    /// orden, ni el resto de valores). Así no se guardan por error cosas que solo valen en esta ejecución: el
    /// <c>--port</c> de la línea de comandos o el <c>Relay</c> que <see cref="Validate"/> apaga por una dirección mala.
    /// Si el archivo no existe, se crea con solo <c>Public</c> (lo demás toma sus valores por defecto al cargarlo).
    /// Se escribe un archivo aparte y se mueve encima, para que un fallo a medias no deje el server.json roto.
    /// </summary>
    /// <exception cref="InvalidDataException">El archivo no es un objeto JSON válido (no se toca).</exception>
    public static void SetPublicInFile(string path, bool value)
    {
        string literal = value ? "true" : "false";
        byte[] edited;
        if (File.Exists(path))
            edited = ReplacePublic(File.ReadAllBytes(path), literal);
        else
            edited = new UTF8Encoding(false).GetBytes("{\n  \"Public\": " + literal + "\n}\n");

        string temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, edited);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // Ya se informa del fallo original.
            }

            throw;
        }
    }

    /// <summary>Devuelve el JSON con el valor de <c>Public</c> cambiado (o añadido al final del objeto si faltaba).</summary>
    private static byte[] ReplacePublic(byte[] original, string literal)
    {
        // Utf8JsonReader no admite la marca de orden de bytes: se aparta y se vuelve a poner.
        int bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF ? 3 : 0;
        var options = new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true };
        var reader = new Utf8JsonReader(original.AsSpan(bom), isFinalBlock: true, new JsonReaderState(options));

        int lastValueEnd = -1;   // dónde termina el último valor de primer nivel (o la "{")
        bool seenProperty = false;
        bool expectPublicValue = false;
        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.StartObject)
                {
                    lastValueEnd = bom + (int)reader.BytesConsumed;
                }
                else if (reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }
                else if (reader.CurrentDepth == 0 && reader.TokenType != JsonTokenType.Comment)
                {
                    throw new InvalidDataException("server.json no es un objeto JSON");
                }
                else if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
                {
                    seenProperty = true;
                    expectPublicValue = reader.ValueTextEquals("Public");
                }
                else if (reader.CurrentDepth == 1 && reader.TokenType is JsonTokenType.True or JsonTokenType.False && expectPublicValue)
                {
                    int start = bom + (int)reader.TokenStartIndex;
                    int length = bom + (int)reader.BytesConsumed - start;
                    return Splice(original, start, length, literal);
                }
                else if (reader.CurrentDepth == 1 && reader.TokenType != JsonTokenType.Comment)
                {
                    // Un valor de primer nivel que no es el de Public (o Public con un valor que no es bool).
                    if (expectPublicValue)
                        throw new InvalidDataException("\"Public\" de server.json no es true ni false");
                    lastValueEnd = bom + (int)reader.BytesConsumed;
                }
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray && reader.CurrentDepth == 1)
                {
                    // Cierra un objeto o lista de primer nivel: ahí termina ese valor.
                    if (expectPublicValue)
                        throw new InvalidDataException("\"Public\" de server.json no es true ni false");
                    lastValueEnd = bom + (int)reader.BytesConsumed;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("server.json no es un JSON válido: " + ex.Message, ex);
        }

        if (lastValueEnd < 0)
            throw new InvalidDataException("server.json no es un objeto JSON");

        string text = Encoding.UTF8.GetString(original);
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        string insertion = (seenProperty ? "," : string.Empty) + newline + "  \"Public\": " + literal;
        return Splice(original, lastValueEnd, 0, insertion);
    }

    private static byte[] Splice(byte[] original, int start, int length, string replacement)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(replacement);
        var result = new byte[original.Length - length + bytes.Length];
        Buffer.BlockCopy(original, 0, result, 0, start);
        Buffer.BlockCopy(bytes, 0, result, start, bytes.Length);
        Buffer.BlockCopy(original, start + length, result, start + bytes.Length, original.Length - start - length);
        return result;
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
