using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DustPlanner.HdtExporter.Logic;

/// <summary>Names timestamped history files and decides which ones to prune. Pure functions; no I/O.</summary>
public static class HistoryRetention
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary><c>collection-20261015-135210.json</c></summary>
    public static string HistoryFileName(string kind, DateTimeOffset exportedAt) =>
        kind + "-" + exportedAt.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".json";

    /// <summary>
    /// Returns the files to delete so that at most <paramref name="keep"/> files of <paramref name="kind"/> remain,
    /// oldest first by the timestamp embedded in the name. Files that do not match the pattern are never deleted.
    /// </summary>
    public static IReadOnlyList<string> FilesToPrune(IEnumerable<string> fileNames, string kind, int keep)
    {
        if (fileNames is null)
        {
            throw new ArgumentNullException(nameof(fileNames));
        }

        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep));
        }

        var prefix = kind + "-";
        var matching = fileNames
            .Select(name => (Name: name, Stamp: TryParseStamp(name, prefix)))
            .Where(x => x.Stamp is not null)
            .OrderBy(x => x.Stamp!.Value)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var excess = matching.Count - keep;
        return excess <= 0 ? Array.Empty<string>() : matching.Take(excess).Select(x => x.Name).ToList();
    }

    private static DateTimeOffset? TryParseStamp(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var stamp = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - ".json".Length);
        return DateTimeOffset.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }
}
