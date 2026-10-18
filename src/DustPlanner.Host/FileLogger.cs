using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Host;

/// <summary>
/// <c>service.log</c> next to the database: the service started by the HDT plugin runs hidden, so without this file the
/// <c>Kick:</c> lines (and every other warning) would be invisible. One line per entry, local time, rotated once at
/// <see cref="MaxBytes"/> (the previous file is kept as <c>service.log.1</c>). Never logs chat content: the callers do
/// not pass it.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    public const string FileName = "service.log";
    public const long MaxBytes = 2 * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private long _bytes;

    public FileLoggerProvider(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _path = Path.Combine(directory, FileName);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, Shorten(categoryName));

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder(160)
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(Level(level)).Append(' ')
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        lock (_gate)
        {
            try
            {
                var writer = Open();
                if (writer is null)
                {
                    return;
                }

                writer.WriteLine(line.ToString());
                writer.Flush();
                _bytes += line.Length + Environment.NewLine.Length;
                if (_bytes > MaxBytes)
                {
                    Rotate();
                }
            }
            catch (IOException)
            {
                // Logging never takes the service down; the next entry tries again.
            }
            catch (UnauthorizedAccessException)
            {
                // same
            }
        }
    }

    private StreamWriter? Open()
    {
        if (_writer is not null)
        {
            return _writer;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _bytes = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        return _writer;
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        _bytes = 0;
        File.Move(_path, _path + ".1", overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary><c>DustPlanner.Kick.KickConnector</c> → <c>KickConnector</c>; framework categories keep their last two parts.</summary>
    private static string Shorten(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 ? category : category[(dot + 1)..];
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
