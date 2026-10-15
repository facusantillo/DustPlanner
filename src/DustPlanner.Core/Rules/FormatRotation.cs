using System.Globalization;
using System.Text.Json.Serialization;

namespace DustPlanner.Core.Rules;

/// <summary>Standard-legal sets per Hearthstone year, from <c>data/format-rotation.json</c>.</summary>
public sealed class FormatRotation
{
    private readonly List<RotationYear> _years;

    public FormatRotation(IEnumerable<RotationYear> years, IEnumerable<string> alwaysStandard, NextRotation next)
    {
        ArgumentNullException.ThrowIfNull(years);
        _years = years.OrderBy(y => y.Starts).ToList();
        if (_years.Count == 0)
        {
            throw new ArgumentException("At least one year is required.", nameof(years));
        }

        AlwaysStandard = alwaysStandard?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        Next = next ?? new NextRotation();
    }

    public IReadOnlyList<RotationYear> Years => _years;

    public IReadOnlySet<string> AlwaysStandard { get; }

    public NextRotation Next { get; }

    public static FormatRotation Load(string? overrideDirectory = null)
    {
        var file = GameDataFiles.Read<RotationFile>(GameDataFiles.FormatRotationFile, overrideDirectory);
        return new FormatRotation(
            file.Years.Select(y => new RotationYear(y.Name, DateOnly.Parse(y.Starts, CultureInfo.InvariantCulture), y.Sets.Distinct(StringComparer.Ordinal).ToList())),
            file.AlwaysStandard,
            new NextRotation(
                file.NextRotation?.Date is { } d ? DateOnly.Parse(d, CultureInfo.InvariantCulture) : null,
                file.NextRotation?.Estimate,
                file.NextRotation?.Note));
    }

    /// <summary>The year in force on <paramref name="date"/> (the latest year whose start is not after it).</summary>
    public RotationYear YearOn(DateOnly date) =>
        _years.LastOrDefault(y => y.Starts <= date) ?? _years[0];

    public RotationYear? PreviousYear(RotationYear year)
    {
        var index = _years.IndexOf(year);
        return index > 0 ? _years[index - 1] : null;
    }

    public bool IsStandard(string set, DateOnly date) => YearOn(date).Sets.Contains(set);

    /// <summary>
    /// Sets expected to leave Standard at the next rotation: sets present in both the current and the previous year,
    /// minus the always-Standard ones. Hearthstone rotates out the previous year's expansions.
    /// </summary>
    public IReadOnlySet<string> RotatingSets(DateOnly date)
    {
        var current = YearOn(date);
        var previous = PreviousYear(current);
        if (previous is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return current.Sets.Where(s => previous.Sets.Contains(s) && !AlwaysStandard.Contains(s)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>All set names known to any year; a set outside this list is "unknown to the rotation table".</summary>
    public IReadOnlySet<string> KnownSets =>
        _years.SelectMany(y => y.Sets).ToHashSet(StringComparer.Ordinal);

    private sealed class RotationFile
    {
        public List<string> AlwaysStandard { get; set; } = new();

        public List<YearFile> Years { get; set; } = new();

        [JsonPropertyName("next_rotation")]
        public NextRotationFile? NextRotation { get; set; }
    }

    private sealed class YearFile
    {
        public string Name { get; set; } = "";

        public string Starts { get; set; } = "";

        public List<string> Sets { get; set; } = new();
    }

    private sealed class NextRotationFile
    {
        public string? Date { get; set; }

        public string? Estimate { get; set; }

        public string? Note { get; set; }
    }
}

public sealed record RotationYear(string Name, DateOnly Starts, IReadOnlyList<string> Sets)
{
    public bool Equals(RotationYear? other) => other is not null && Name == other.Name && Starts == other.Starts;

    public override int GetHashCode() => HashCode.Combine(Name, Starts);
}

/// <summary>When the next rotation happens: an announced date, or an estimate label such as <c>2027-03</c>.</summary>
public sealed record NextRotation(DateOnly? Date = null, string? Estimate = null, string? Note = null)
{
    public string Display => Date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : (Estimate is null ? "unknown" : Estimate + " (estimated)");

    /// <summary>
    /// True when <paramref name="date"/> is on or after the announced date, or after the month of the estimate
    /// (<c>2027-03</c> → from 2027-04-01): the data file then lacks the year in force and Standard legality is stale.
    /// </summary>
    public bool IsPast(DateOnly date)
    {
        if (Date is { } announced)
        {
            return date >= announced;
        }

        if (Estimate is not null && DateOnly.TryParseExact(Estimate + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return date >= month.AddMonths(1);
        }

        return false;
    }
}
