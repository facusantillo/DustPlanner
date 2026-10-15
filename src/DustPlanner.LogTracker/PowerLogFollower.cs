using System.Text;

namespace DustPlanner.LogTracker;

/// <summary>
/// Tails the newest Power.log: each <see cref="Poll"/> reads the lines appended since the last call and feeds them to
/// a parser, switching to a newer session folder when the game is relaunched. Designed for a timer loop (the overlay
/// host polls every few seconds); no file-system watchers, no hooks.
/// </summary>
public sealed class PowerLogFollower
{
    private readonly string _logsDirectory;
    private readonly TimeZoneInfo? _timeZone;
    private string? _currentPath;
    private long _offset;
    private PowerLogParser? _parser;
    private readonly StringBuilder _partial = new();

    /// <param name="logsDirectory">Hearthstone's <c>Logs</c> folder.</param>
    /// <param name="timeZone">Zone of the log's clock times (default: this machine's local zone, which is what the game writes).</param>
    public PowerLogFollower(string logsDirectory, TimeZoneInfo? timeZone = null)
    {
        _logsDirectory = logsDirectory ?? throw new ArgumentNullException(nameof(logsDirectory));
        _timeZone = timeZone;
    }

    /// <summary>Raised for each game completed since the follower started.</summary>
    public event Action<GameRecord>? GameCompleted;

    public string? CurrentPath => _currentPath;

    /// <summary>The game in progress according to the log, if any.</summary>
    public GameRecord? CurrentGame => _parser?.Current;

    /// <summary>
    /// Reads newly appended lines. When <paramref name="skipExisting"/> is true on the first call, the current file
    /// content is skipped so only games played from now on are reported (the importer handles history).
    /// </summary>
    public int Poll(bool skipExisting = true)
    {
        var logs = LogLocator.FindPowerLogs(_logsDirectory);
        if (logs.Count == 0)
        {
            return 0;
        }

        var newest = logs[^1];

        if (!string.Equals(newest.Path, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            var first = _currentPath is null;
            var sameSession = _currentPath is not null
                && string.Equals(Path.GetDirectoryName(_currentPath), newest.Session, StringComparison.OrdinalIgnoreCase);
            if (sameSession && _parser is not null)
            {
                // HDT restarted and renamed Power.log to Power_old.log (the game keeps writing to the renamed file
                // until it opens a fresh Power.log): same session, same clock, same game in progress. Keep the
                // parser; keep the offset when the file is the renamed one, start at 0 when it is the fresh one.
                var renamed = newest.Rotated && new FileInfo(newest.Path).Length >= _offset;
                _currentPath = newest.Path;
                if (!renamed)
                {
                    _offset = 0;
                    _partial.Clear();
                }
            }
            else
            {
                _currentPath = newest.Path;
                _parser = new PowerLogParser(newest.BaseDate, _timeZone);
                _partial.Clear();
                _offset = 0;
                if (first && skipExisting)
                {
                    // Replay what is already in the file without raising events: games played before the follower
                    // started are not reported, but the parser keeps its clock (midnight rollovers) and the game in
                    // progress, so CurrentGame and later timestamps are right even when we start mid-session.
                    _offset = ReadAvailable();
                }

                _parser.GameCompleted += g => GameCompleted?.Invoke(g);
            }
        }

        ReadAvailable();
        return _linesLastRead;
    }

    private int _linesLastRead;

    /// <summary>Reads from the current offset to the end of the file, feeding complete lines to the parser. Returns the new offset.</summary>
    private long ReadAvailable()
    {
        using var stream = new FileStream(_currentPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _offset)
        {
            _offset = 0; // truncated / rewritten
            _partial.Clear();
        }

        stream.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var lines = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            _offset += read;
            _partial.Append(Encoding.UTF8.GetString(buffer, 0, read));
            lines += DrainCompleteLines();
        }

        _linesLastRead = lines;
        return _offset;
    }

    private int DrainCompleteLines()
    {
        var count = 0;
        while (true)
        {
            var text = _partial.ToString();
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            if (newline < 0)
            {
                return count;
            }

            var line = text[..newline].TrimEnd('\r');
            _partial.Remove(0, newline + 1);
            _parser!.Feed(line);
            count++;
        }
    }
}
