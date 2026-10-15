using DustPlanner.Core.Cards;
using DustPlanner.Core.Deckstrings;

namespace DustPlanner.Core.Planner;

/// <summary>How often a card shows up in the meta decks the user pasted into <c>meta.txt</c>.</summary>
public sealed class MetaSignal
{
    private readonly Dictionary<int, double> _share;
    private readonly Dictionary<int, HashSet<string>> _classes;

    private MetaSignal(int deckCount, Dictionary<int, double> share, Dictionary<int, HashSet<string>> classes)
    {
        DeckCount = deckCount;
        _share = share;
        _classes = classes;
    }

    public static MetaSignal Empty { get; } = new(0, new Dictionary<int, double>(), new Dictionary<int, HashSet<string>>());

    public int DeckCount { get; }

    /// <summary>Fraction of meta decks (0..1) that contain the card.</summary>
    public double Share(int dbfId) => _share.GetValueOrDefault(dbfId);

    /// <summary>Classes of the meta decks that contain the card (from the hero card or the deck's class cards).</summary>
    public IReadOnlySet<string> ClassesUsing(int dbfId) =>
        _classes.TryGetValue(dbfId, out var set) ? set : new HashSet<string>(StringComparer.Ordinal);

    public static MetaSignal FromDecks(IReadOnlyList<DeckCode> decks, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(catalog);
        if (decks.Count == 0)
        {
            return Empty;
        }

        var counts = new Dictionary<int, int>();
        var classes = new Dictionary<int, HashSet<string>>();
        foreach (var deck in decks)
        {
            var deckClass = MissingCards.DeckClassOf(deck, catalog);
            var ids = deck.Cards.Select(c => c.DbfId).Concat(deck.Sideboards.Select(s => s.DbfId)).Distinct();
            foreach (var id in ids)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
                if (deckClass is not null)
                {
                    (classes.TryGetValue(id, out var set) ? set : classes[id] = new HashSet<string>(StringComparer.Ordinal)).Add(deckClass);
                }
            }
        }

        return new MetaSignal(decks.Count, counts.ToDictionary(p => p.Key, p => (double)p.Value / decks.Count), classes);
    }

    public static MetaSignal FromText(string metaText, CardCatalog catalog) =>
        FromDecks(DeckBlockParser.ParseMany(metaText), catalog);
}
