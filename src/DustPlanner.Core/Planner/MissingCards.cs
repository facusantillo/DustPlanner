using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Planner;

/// <summary>One deck card compared against the collection.</summary>
public sealed record DeckCardStatus(
    CardDef Card,
    int Needed,
    int Owned,
    int Missing,
    bool InSideboard,
    CraftabilityStatus Craftability,
    int? CraftCostEach,
    bool IsStandard,
    bool RotatesNext)
{
    /// <summary>Why the card is not craftable (the game's wording, or the reward unlock text); null when craftable.</summary>
    public string? CraftabilityReason { get; init; }

    /// <summary>Dust to craft the missing copies; null when they cannot be crafted (or craftability is unknown).</summary>
    public int? CraftCostMissing => Missing > 0 && Craftability == CraftabilityStatus.Craftable && CraftCostEach is { } each ? Missing * each : null;
}

public sealed record UnknownDeckCard(int DbfId, int Count);

/// <summary>A deck slot taken by a non-collectible companion (Fabled legendaries ship with theirs): comes with <see cref="Parent"/>, never crafted.</summary>
public sealed record CompanionDeckCard(CompanionCard Companion, CardDef Parent, int Count);

public sealed record RotationRisk(string NextRotation, IReadOnlyList<DeckCardStatus> Cards, int DustAtRisk);

public sealed record MissingReport(
    DeckCode Deck,
    string? DeckName,
    string? DeckClass,
    DateOnly AsOf,
    IReadOnlyList<DeckCardStatus> Cards,
    IReadOnlyList<UnknownDeckCard> UnknownCards,
    int DustAvailable,
    RotationRisk Rotation,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Deck slots filled by companions of a collectible card (see <see cref="CompanionDeckCard"/>).</summary>
    public IReadOnlyList<CompanionDeckCard> Companions { get; init; } = Array.Empty<CompanionDeckCard>();

    public IEnumerable<DeckCardStatus> Missing => Cards.Where(c => c.Missing > 0);

    public IEnumerable<DeckCardStatus> MissingCraftable => Missing.Where(c => c.Craftability == CraftabilityStatus.Craftable);

    public IEnumerable<DeckCardStatus> MissingNotCraftable => Missing.Where(c => c.Craftability == CraftabilityStatus.NotCraftable);

    public IEnumerable<DeckCardStatus> MissingUnknownCraftability => Missing.Where(c => c.Craftability == CraftabilityStatus.Unknown);

    /// <summary>Dust needed to craft every missing craftable copy.</summary>
    public int TotalCraftCost => MissingCraftable.Sum(c => c.CraftCostMissing ?? 0);

    /// <summary>Dust still to obtain after spending what you have (never negative).</summary>
    public int Shortfall => Math.Max(0, TotalCraftCost - DustAvailable);

    public bool Complete => !Missing.Any() && UnknownCards.Count == 0;
}

/// <summary>Computes what a deck needs from a collection.</summary>
public static class MissingCards
{
    public static MissingReport Analyze(DeckCode deck, CollectionSnapshot collection, CardCatalog catalog, GameRules rules, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rules);

        // Main deck + sideboards both consume collection copies.
        var needed = new Dictionary<int, (int Count, bool Sideboard)>();
        foreach (var card in deck.Cards)
        {
            needed[card.DbfId] = (needed.GetValueOrDefault(card.DbfId).Count + card.Count, false);
        }

        foreach (var side in deck.Sideboards)
        {
            var existing = needed.GetValueOrDefault(side.DbfId);
            needed[side.DbfId] = (existing.Count + side.Count, existing.Count == 0 || existing.Sideboard);
        }

        var rotating = rules.Rotation.RotatingSets(asOf);
        var statuses = new List<DeckCardStatus>();
        var unknown = new List<UnknownDeckCard>();
        var companions = new List<CompanionDeckCard>();
        foreach (var (dbfId, (count, sideboard)) in needed)
        {
            if (!catalog.TryGet(dbfId, out var card))
            {
                if (catalog.TryGetCompanion(dbfId, out var companion, out var parent))
                {
                    companions.Add(new CompanionDeckCard(companion, parent, count));
                }
                else
                {
                    unknown.Add(new UnknownDeckCard(dbfId, count));
                }

                continue;
            }

            var owned = collection.TotalCopies(dbfId);
            var (craftability, why) = rules.Craftability.Explain(card);
            statuses.Add(new DeckCardStatus(
                card,
                count,
                Math.Min(owned, count),
                Math.Max(0, count - owned),
                sideboard,
                craftability,
                rules.Dust.CraftCost(card.Rarity, PremiumKind.Normal),
                rules.Rotation.IsStandard(card.Set, asOf),
                rotating.Contains(card.Set)) { CraftabilityReason = why });
        }

        statuses.Sort((a, b) => a.Card.Cost != b.Card.Cost ? a.Card.Cost.CompareTo(b.Card.Cost) : string.CompareOrdinal(a.Card.Name, b.Card.Name));

        var warnings = new List<string>();
        if (deck.Format == DeckFormat.Standard)
        {
            var notStandard = statuses.Where(s => !s.IsStandard).ToList();
            if (notStandard.Count > 0)
            {
                warnings.Add($"Deck says Standard but {notStandard.Count} card(s) are not Standard-legal on {asOf:yyyy-MM-dd}: " +
                             string.Join(", ", notStandard.Take(5).Select(s => s.Card.Name)) + (notStandard.Count > 5 ? ", …" : string.Empty));
            }
        }
        else if (deck.Format == DeckFormat.Wild)
        {
            warnings.Add("Deck code is Wild format.");
        }

        if (unknown.Count > 0)
        {
            warnings.Add($"{unknown.Count} card id(s) are not in the card catalog (build {catalog.Build}); run 'dustplanner catalog update'.");
        }

        var unknownSets = statuses.Where(s => s.Craftability == CraftabilityStatus.Unknown).Select(s => s.Card.Set).Distinct(StringComparer.Ordinal).ToList();
        if (unknownSets.Count > 0)
        {
            warnings.Add("Craftability unknown for set(s) " + string.Join(", ", unknownSets) + "; add them to data/non-craftable.json.");
        }

        if (rules.Rotation.Next.IsPast(asOf) && rules.Rotation.YearOn(asOf) == rules.Rotation.Years[^1])
        {
            warnings.Add($"data/format-rotation.json knows no Standard year after {rules.Rotation.Years[^1].Name} ({rules.Rotation.Years[^1].Starts:yyyy-MM-dd}) " +
                         $"but the next rotation ({rules.Rotation.Next.Display}) is already past on {asOf:yyyy-MM-dd}: Standard legality and rotation risk may be stale; update the data file.");
        }

        var age = DateTimeOffset.UtcNow - collection.TakenAt;
        if (age > TimeSpan.FromDays(7))
        {
            warnings.Add($"Collection snapshot is {age.Days} days old ({collection.Source}, {collection.TakenAt:yyyy-MM-dd}).");
        }

        var rotatingCards = statuses.Where(s => s.RotatesNext).ToList();
        var rotation = new RotationRisk(rules.Rotation.Next.Display, rotatingCards, rotatingCards.Sum(c => c.CraftCostMissing ?? 0));

        return new MissingReport(deck, deck.Name, DeckClassOf(deck, catalog), asOf, statuses, unknown, collection.Dust, rotation, warnings) { Companions = companions };
    }

    /// <summary>Class of the deck from its hero card (e.g. <c>PRIEST</c>), when the hero is in the catalog.</summary>
    public static string? DeckClassOf(DeckCode deck, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var hero in deck.Heroes)
        {
            if (catalog.TryGet(hero, out var card) && !card.IsNeutral)
            {
                return card.CardClass;
            }
        }

        // Fallback: the only non-neutral class among the deck's cards.
        var classes = deck.Cards.Select(c => catalog.GetByDbfId(c.DbfId)).Where(c => c is not null && !c.IsNeutral).Select(c => c!.CardClass).Distinct(StringComparer.Ordinal).ToList();
        return classes.Count == 1 ? classes[0] : null;
    }
}
