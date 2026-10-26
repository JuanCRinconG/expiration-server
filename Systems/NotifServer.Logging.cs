#nullable enable


/// <summary>
/// Monthly rolling TXT logs (<c>expiration-notif-YYYY-MM.txt</c>, UTC month).
/// Directory comes from JSON <c>logDirectory</c> (default <c>logs</c> next to the exe).
/// </summary>
public static partial class NotifServer
{
    private const string DefaultLogDirectoryName = "logs";
    private const string LogFilePrefix = "expiration-notif-";

    private static readonly object LogLock = new();
    private static string _logDirectory = Path.Combine(AppContext.BaseDirectory, DefaultLogDirectoryName);
    private static bool _loggedWriteFailure;

    /// <summary>
    /// Sets the directory for monthly log files. Relative paths are resolved against
    /// <see cref="AppContext.BaseDirectory"/>. Empty / whitespace keeps the default <c>logs</c> folder.
    /// </summary>
    private static void ConfigureLogDirectory(string? logDirectory)
    {
        string resolved = ResolveLogDirectory(logDirectory);
        lock (LogLock)
        {
            _logDirectory = resolved;
            _loggedWriteFailure = false;
        }
    }

    private static string ResolveLogDirectory(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DefaultLogDirectoryName));

        string trimmed = logDirectory.Trim();
        if (Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));
    }

    /// <summary>Full path of this UTC month’s log file (directory may not exist yet).</summary>
    private static string GetCurrentLogFilePath()
    {
        string month = DateTime.UtcNow.ToString("yyyy-MM");
        string dir;
        lock (LogLock)
            dir = _logDirectory;

        return Path.Combine(dir, $"{LogFilePrefix}{month}.txt");
    }

    private static void LogInfo(string message) => WriteLog("INFO", message);

    private static void LogWarn(string message) => WriteLog("WARN", message);

    private static void WriteLog(string level, string message)
    {
        string line = $"[{Stamp()}] {level}  {message}";
        string path = GetCurrentLogFilePath();

        lock (LogLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                if (!_loggedWriteFailure)
                {
                    _loggedWriteFailure = true;
                    BackendLog.Error(
                        $"[{Stamp()}] WARN  Could not write log file '{path}': {ex.Message}. "
                        + "Further log I/O errors are suppressed; falling back to stderr for this line.");
                }

                BackendLog.Error(line);
            }
        }
    }
}
