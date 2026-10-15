namespace DustPlanner.Core.Cards;

/// <summary>Immutable index of collectible cards keyed by dbfId and card id.</summary>
public sealed class CardCatalog
{
    private readonly Dictionary<int, CardDef> _byDbfId;
    private readonly Dictionary<string, CardDef> _byCardId;
    private readonly ILookup<string, CardDef> _byName;

    public CardCatalog(IEnumerable<CardDef> cards, string build, string locale)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var list = cards.ToList();
        _byDbfId = list.ToDictionary(c => c.DbfId);
        _byCardId = list.ToDictionary(c => c.CardId, StringComparer.Ordinal);
        _byName = list.ToLookup(c => c.Name, StringComparer.OrdinalIgnoreCase);
        Build = build;
        Locale = locale;
    }

    /// <summary>Hearthstone client build the catalog was generated from (as reported by HearthstoneJSON), or a label.</summary>
    public string Build { get; }

    public string Locale { get; }

    public int Count => _byDbfId.Count;

    public IEnumerable<CardDef> All => _byDbfId.Values;

    public CardDef? GetByDbfId(int dbfId) => _byDbfId.GetValueOrDefault(dbfId);

    public CardDef? GetByCardId(string cardId) => _byCardId.GetValueOrDefault(cardId);

    public IReadOnlyList<CardDef> FindByName(string name) => _byName[name].ToList();

    public bool TryGet(int dbfId, out CardDef card)
    {
        if (_byDbfId.TryGetValue(dbfId, out var found))
        {
            card = found;
            return true;
        }

        card = null!;
        return false;
    }

    /// <summary>Distinct set names present in the catalog, useful to detect sets the rule files do not know yet.</summary>
    public IReadOnlyCollection<string> Sets => _byDbfId.Values.Select(c => c.Set).Distinct(StringComparer.Ordinal).ToList();
}
