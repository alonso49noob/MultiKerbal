namespace MultiKerbal.Relay;

internal static class RelayLog
{
    private static readonly object Sync = new();
    private static StreamWriter? _file;

    /// <summary>Carpeta del registro diario (<see cref="OpenDaily"/>); null con un archivo fijo o sin archivo.</summary>
    private static string? _directory;

    /// <summary>Día del archivo diario abierto ahora.</summary>
    private static DateTime _fileDay;

    public static bool ConsoleEnabled { get; set; } = true;

    /// <summary>Hora local de cada línea y del cambio de archivo. Los tests la cambian.</summary>
    internal static Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>Recibe cada línea escrita (para los tests).</summary>
    internal static Action<string>? Listener { get; set; }

    /// <summary>Escribe en un archivo fijo.</summary>
    public static void OpenFile(string path)
    {
        lock (Sync)
        {
            _directory = null;
            Open(path);
        }
    }

    /// <summary>Escribe en <c>relay-AAAAMMDD.log</c> dentro de <paramref name="directory"/>, con un archivo nuevo cada día.</summary>
    public static void OpenDaily(string directory)
    {
        lock (Sync)
        {
            _directory = Path.GetFullPath(directory);
            _fileDay = Clock().Date;
            Open(DailyPath(_fileDay));
        }
    }

    public static void Close()
    {
        lock (Sync)
        {
            _file?.Dispose();
            _file = null;
            _directory = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, ConsoleColor.Gray);

    public static void Warn(string message) => Write("WARN", message, ConsoleColor.Yellow);

    public static void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    private static string DailyPath(DateTime day) => Path.Combine(_directory!, $"relay-{day:yyyyMMdd}.log");

    /// <summary>Con <see cref="Sync"/>.</summary>
    private static void Open(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var file = new StreamWriter(fullPath, append: true) { AutoFlush = true };
        _file?.Dispose();
        _file = file;
    }

    private static void Write(string level, string message, ConsoleColor color)
    {
        lock (Sync)
        {
            DateTime now = Clock();
            string line = $"[{now:HH:mm:ss}] [{level}] {message}";
            if (ConsoleEnabled)
            {
                ConsoleColor previous = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = previous;
            }

            if (_directory != null && now.Date != _fileDay)
            {
                // Cambio de día: archivo nuevo. Si no se puede abrir, se sigue con el de ayer.
                try
                {
                    Open(DailyPath(now.Date));
                    _fileDay = now.Date;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            _file?.WriteLine(line);
            Listener?.Invoke(line);
        }
    }
}
