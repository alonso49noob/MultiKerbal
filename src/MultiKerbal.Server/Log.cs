namespace MultiKerbal.Server;

internal static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _file;

    public static bool ConsoleEnabled { get; set; } = true;

    public static void OpenFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        lock (Sync)
        {
            _file?.Dispose();
            _file = new StreamWriter(fullPath, append: true) { AutoFlush = true };
        }
    }

    public static void Close()
    {
        lock (Sync)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, ConsoleColor.Gray);

    public static void Warn(string message) => Write("WARN", message, ConsoleColor.Yellow);

    public static void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    public static void Chat(string message) => Write("CHAT", message, ConsoleColor.Cyan);

    private static void Write(string level, string message, ConsoleColor color)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (Sync)
        {
            if (ConsoleEnabled)
            {
                ConsoleColor previous = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = previous;
            }

            _file?.WriteLine(line);
        }
    }
}
