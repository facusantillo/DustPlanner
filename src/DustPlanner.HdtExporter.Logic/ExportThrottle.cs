using System;

namespace DustPlanner.HdtExporter.Logic;

/// <summary>
/// Decides whether an export should run: skips when the content hash is unchanged, and rate-limits
/// repeated exports of the same kind. Manual exports always run.
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

        if (contentHash is not null && string.Equals(contentHash, _lastHash, StringComparison.Ordinal))
        {
            return ThrottleDecision.SkipUnchanged;
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
