namespace DustPlanner.Core.Cards;

/// <summary>Immutable index of collectible cards keyed by dbfId and card id.</summary>
public sealed class CardCatalog
{
    private readonly Dictionary<int, CardDef> _byDbfId;
    private readonly Dictionary<string, CardDef> _byCardId;
    private readonly ILookup<string, CardDef> _byName;

    private readonly Dictionary<int, CompanionCard> _companions;

    public CardCatalog(IEnumerable<CardDef> cards, string build, string locale)
        : this(cards, Array.Empty<CompanionCard>(), build, locale)
    {
    }

    public CardCatalog(IEnumerable<CardDef> cards, IEnumerable<CompanionCard> companions, string build, string locale)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(companions);
        var list = cards.ToList();
        _byDbfId = list.ToDictionary(c => c.DbfId);
        _byCardId = list.ToDictionary(c => c.CardId, StringComparer.Ordinal);
        _byName = list.ToLookup(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _companions = companions.Where(c => _byDbfId.ContainsKey(c.ParentDbfId)).ToDictionary(c => c.DbfId);
        Build = build;
        Locale = locale;
    }

    /// <summary>A copy of this catalog with the <c>howToEarn</c> texts taken from <paramref name="reference"/> (the English catalog), matched by dbfId.</summary>
    public CardCatalog WithHowToEarnFrom(CardCatalog reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var cards = _byDbfId.Values.Select(c => reference.GetByDbfId(c.DbfId) is { } en ? c with { HowToEarn = en.HowToEarn, HowToEarnGolden = en.HowToEarnGolden } : c);
        return new CardCatalog(cards, _companions.Values, Build, Locale);
    }

    /// <summary>Non-collectible cards that come with a collectible one (Fabled companions and other tokens), by dbfId.</summary>
    public IReadOnlyDictionary<int, CompanionCard> Companions => _companions;

    /// <summary>True when <paramref name="dbfId"/> is a token of a collectible card; <paramref name="parent"/> is that card.</summary>
    public bool TryGetCompanion(int dbfId, out CompanionCard companion, out CardDef parent)
    {
        if (_companions.TryGetValue(dbfId, out var found) && _byDbfId.TryGetValue(found.ParentDbfId, out var owner))
        {
            companion = found;
            parent = owner;
            return true;
        }

        companion = null!;
        parent = null!;
        return false;
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
