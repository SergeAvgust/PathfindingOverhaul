namespace PathfindingFix;

// The mod's log, data\Logs\PathfindingFix.log next to the game's own logs (the temp folder when that cannot be written).
// Info lines are always written (start-up, errors, reports); Verbose lines only when the LogEnabled setting is on.
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static bool Verbose { get; set; }

    private static string Path_()
    {
        if (_path != null)
        {
            return _path;
        }
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "data", "Logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "PathfindingFix.log");
        }
        catch
        {
            _path = Path.Combine(Path.GetTempPath(), "PathfindingFix.log");
        }
        return _path;
    }

    // A new game session starts a new log.
    public static void Start()
    {
        try
        {
            lock (Gate)
            {
                File.WriteAllText(Path_(), "");
            }
        }
        catch
        {
            // logging must never break the game
        }
    }

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path_(), $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the game
        }
    }

    public static void Detail(string message)
    {
        if (Verbose)
        {
            Info(message);
        }
    }
}
