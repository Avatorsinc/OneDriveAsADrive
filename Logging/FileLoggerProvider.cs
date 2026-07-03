using System.Collections.Concurrent;
using System.Text;

namespace OneDriveAsADrive.Logging;

// When it runs hidden in the background there's no console to watch, so we tee everything
// to a log file the user (or an admin) can tail later. Peter keeps a diary too; his is worse.
//
// Deliberately tiny — no external logging package, no rolling framework. On startup we trim
// the file if it got fat (>2 MB), and every write appends a line under a lock. That's it.
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 2 * 1024 * 1024; // rotate past 2 MB

    private readonly string _path;
    private readonly LogLevel _min;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private long _written;

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneDriveAsADrive", "logs", "app.log");

    public FileLoggerProvider(string? path = null, LogLevel minLevel = LogLevel.Information)
    {
        _path = path ?? DefaultLogPath;
        _min = minLevel;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        try { _written = File.Exists(_path) ? new FileInfo(_path).Length : 0; }
        catch { _written = 0; }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _min, Write));

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                // Rotate BEFORE the background process (which runs for days between logons)
                // can grow the log without bound. Keep one previous file as app.log.1.
                if (_written > MaxBytes)
                {
                    var backup = _path + ".1";
                    File.Delete(backup);
                    File.Move(_path, backup);
                    _written = 0;
                }
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    fs.Write(bytes, 0, bytes.Length);
                _written += bytes.Length;
            }
            catch { /* if we can't log, we can't log. Life goes on. */ }
        }
    }

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger(string category, LogLevel min, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= min;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var msg = formatter(state, ex);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Short(level)}] {category}: {msg}";
            if (ex != null) line += Environment.NewLine + ex;
            write(line);
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "trc",
            LogLevel.Debug => "dbg",
            LogLevel.Information => "inf",
            LogLevel.Warning => "wrn",
            LogLevel.Error => "err",
            LogLevel.Critical => "crt",
            _ => "???"
        };
    }
}
