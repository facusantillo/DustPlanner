using System;

namespace DustPlanner.HdtExporter.Logic;

/// <summary>
/// Decides whether an export should run. Content-hashed exports (the collection) run whenever the content changed and
/// are skipped while it is unchanged: a changed collection is never rate-limited, because HDT fires its collection
/// event exactly once when the player leaves the collection screen and that event must not be lost. Exports without a
/// hash (decks, rank) are rate-limited by <c>minInterval</c>. Manual exports always run.
/// </summary>
public sealed class ExportThrottle
{
    private readonly TimeSpan _minInterval;
    private DateTimeOffset? _lastRun;
    private string? _lastHash;

    public ExportThrottle(TimeSpan minInterval)
    {
        _minInterval = minInterval;
    }

    public string? LastHash => _lastHash;

    public ThrottleDecision Decide(DateTimeOffset now, string? contentHash, bool manual)
    {
        if (manual)
        {
            return ThrottleDecision.Run;
        }

        if (contentHash is not null)
        {
            return string.Equals(contentHash, _lastHash, StringComparison.Ordinal) ? ThrottleDecision.SkipUnchanged : ThrottleDecision.Run;
        }

        if (_lastRun is not null && now - _lastRun.Value < _minInterval)
        {
            return ThrottleDecision.SkipTooSoon;
        }

        return ThrottleDecision.Run;
    }

    public void MarkRan(DateTimeOffset now, string? contentHash)
    {
        _lastRun = now;
        _lastHash = contentHash;
    }
}

public enum ThrottleDecision
{
    Run,
    SkipUnchanged,
    SkipTooSoon,
}
