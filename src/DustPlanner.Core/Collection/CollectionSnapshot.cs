using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Collection;

/// <summary>Copies of one card, per premium kind, plus the raw HDT array for diagnostics.</summary>
public sealed record OwnedCard(int DbfId, IReadOnlyDictionary<PremiumKind, int> Copies, int[] RawCounts)
{
    public int Count(PremiumKind kind) => Copies.GetValueOrDefault(kind);

    /// <summary>All copies that can be put in a deck, whatever the premium kind (unknown kinds included).</summary>
    public int TotalCopies => Copies.Values.Sum();

    /// <summary>Copies whose dust value can be computed (normal + golden, plus signature/diamond when rules know them).</summary>
    public int KnownCopies => Copies.Where(p => p.Key != PremiumKind.Unknown).Sum(p => p.Value);
}

/// <summary>A collection snapshot in the planner's own shape, independent of where it was imported from.</summary>
public sealed class CollectionSnapshot
{
    private readonly Dictionary<int, OwnedCard> _cards;

    public CollectionSnapshot(string accountKey, DateTimeOffset takenAt, string source, int dust, IEnumerable<OwnedCard> cards,
        int? gold = null, int? unopenedPacks = null, string? battleTag = null, string? region = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        AccountKey = accountKey;
        TakenAt = takenAt;
        Source = source;
        Dust = dust;
        Gold = gold;
        UnopenedPacks = unopenedPacks;
        BattleTag = battleTag;
        Region = region;
        _cards = cards.Where(c => c.TotalCopies > 0).ToDictionary(c => c.DbfId);
    }

    public string AccountKey { get; }

    public DateTimeOffset TakenAt { get; }

    /// <summary><c>hdt</c>, <c>hsreplay</c>, <c>csv</c>, ...</summary>
    public string Source { get; }

    public int Dust { get; }

    public int? Gold { get; }

    /// <summary>Always null today (no API exposes it); kept for the "open your packs" warning.</summary>
    public int? UnopenedPacks { get; }

    public string? BattleTag { get; }

    public string? Region { get; }

    public IReadOnlyCollection<OwnedCard> Cards => _cards.Values;

    public int DistinctCards => _cards.Count;

    public OwnedCard? Get(int dbfId) => _cards.GetValueOrDefault(dbfId);

    public int Copies(int dbfId, PremiumKind kind) => _cards.TryGetValue(dbfId, out var card) ? card.Count(kind) : 0;

    public int TotalCopies(int dbfId) => _cards.TryGetValue(dbfId, out var card) ? card.TotalCopies : 0;

    /// <summary>Builds an <see cref="OwnedCard"/> from HDT's 8-int array using the slot map (trial copies are ignored).</summary>
    public static OwnedCard FromHdtCounts(int dbfId, int[] counts, PremiumSlotMap slots)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(slots);
        var copies = new Dictionary<PremiumKind, int>();
        for (var slot = 0; slot < slots.SlotCount && slot < counts.Length; slot++)
        {
            if (counts[slot] <= 0)
            {
                continue;
            }

            var kind = slots.KindOf(slot);
            copies[kind] = copies.GetValueOrDefault(kind) + counts[slot];
        }

        return new OwnedCard(dbfId, copies, (int[])counts.Clone());
    }
}
