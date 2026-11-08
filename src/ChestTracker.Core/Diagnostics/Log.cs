using System.Globalization;

namespace ChestTracker.Core.Diagnostics;

/// <summary>Tiny append-only file logger with single-file rotation. Never throws.</summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _file;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _file = Path.Combine(directory, "app.log");
            var info = new FileInfo(_file);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Copy(_file, Path.Combine(directory, "app.1.log"), overwrite: true);
                File.Delete(_file);
            }
        }
        catch
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var file = _file;
        if (file is null)
        {
            return;
        }

        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
        try
        {
            lock (Gate)
            {
                File.AppendAllText(file, line);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
