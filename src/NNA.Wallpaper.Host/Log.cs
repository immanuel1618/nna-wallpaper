using System.Collections.Concurrent;
using System.Text;

namespace NNA.Wallpaper.Host;

/// <summary>
/// Minimal file logger: one line per entry, rotates at 1 MB (app.log -> app.log.1),
/// keeps the last 200 lines in memory for diagnostics. Never logs tokens or session data.
/// </summary>
public sealed class Log
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<string> _tail = new();
    private const int TailSize = 200;
    private const long RotateBytes = 1_000_000;

    public Log(Paths paths)
    {
        Directory.CreateDirectory(paths.LogsDir);
        _file = paths.LogFile;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex is null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message);

    public IReadOnlyList<string> Tail() => _tail.ToArray();

    private void Write(string level, string message)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message;
        _tail.Enqueue(line);
        while (_tail.Count > TailSize && _tail.TryDequeue(out _)) { }
        lock (_lock)
        {
            try
            {
                var fi = new FileInfo(_file);
                if (fi.Exists && fi.Length > RotateBytes)
                {
                    File.Move(_file, _file + ".1", overwrite: true);
                }
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // logging must never take the app down
            }
        }
    }
}
