using System.Text.Json;
using DustPlanner.Core.Json;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Planner;

/// <summary>A full-dust refund window for a nerfed card, as the user records it in <c>refunds.json</c>.</summary>
public sealed class RefundEntry
{
    public int DbfId { get; set; }

    /// <summary><c>normal</c>, <c>golden</c>, <c>signature</c>, <c>diamond</c> or <c>all</c>.</summary>
    public string Premium { get; set; } = "all";

    public DateOnly StartsAt { get; set; }

    public DateOnly EndsAt { get; set; }

    public string? Note { get; set; }

    public bool AppliesTo(PremiumKind kind, DateOnly asOf) =>
        asOf >= StartsAt && asOf <= EndsAt
        && (string.Equals(Premium, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Premium, kind.ToString(), StringComparison.OrdinalIgnoreCase));
}

public sealed class Refunds
{
    public const string FileName = "refunds.json";

    private readonly ILookup<int, RefundEntry> _byCard;

    public Refunds(IEnumerable<RefundEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _byCard = entries.ToLookup(e => e.DbfId);
    }

    public static Refunds Empty { get; } = new(Array.Empty<RefundEntry>());

    public int Count => _byCard.Count;

    public RefundEntry? Active(int dbfId, PremiumKind kind, DateOnly asOf) =>
        _byCard[dbfId].FirstOrDefault(e => e.AppliesTo(kind, asOf));

    public static Refunds Load(string path)
    {
        var entries = JsonSerializer.Deserialize<List<RefundEntry>>(File.ReadAllText(path), CoreJson.Options) ?? new List<RefundEntry>();
        return new Refunds(entries);
    }

    public static Refunds LoadOrEmpty(string? path) => path is not null && File.Exists(path) ? Load(path) : Empty;
}
