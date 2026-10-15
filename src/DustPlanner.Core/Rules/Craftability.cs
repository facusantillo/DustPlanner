using DustPlanner.Core.Cards;

namespace DustPlanner.Core.Rules;

public enum CraftabilityStatus
{
    /// <summary>Set, rarity and type are all known to be craftable.</summary>
    Craftable,

    /// <summary>Known not to be craftable (Core set, free rarity, uncraftable id, ...).</summary>
    NotCraftable,

    /// <summary>The set is in neither list; treated as not craftable for dust purposes, and reported.</summary>
    Unknown,
}

/// <summary>Craftability rules from <c>data/non-craftable.json</c>.</summary>
public sealed class Craftability
{
    private readonly HashSet<string> _craftableSets;
    private readonly HashSet<string> _nonCraftableSets;
    private readonly HashSet<string> _craftableRarities;
    private readonly HashSet<string> _craftableTypes;
    private readonly HashSet<string> _uncraftableCardIds;
    private readonly HashSet<string> _uncraftableGoldenCardIds;

    public Craftability(
        IEnumerable<string> craftableSets,
        IEnumerable<string> nonCraftableSets,
        IEnumerable<string> craftableRarities,
        IEnumerable<string> craftableTypes,
        IEnumerable<string> uncraftableCardIds,
        IEnumerable<string> uncraftableGoldenCardIds)
    {
        _craftableSets = craftableSets.ToHashSet(StringComparer.Ordinal);
        _nonCraftableSets = nonCraftableSets.ToHashSet(StringComparer.Ordinal);
        _craftableRarities = craftableRarities.ToHashSet(StringComparer.Ordinal);
        _craftableTypes = craftableTypes.ToHashSet(StringComparer.Ordinal);
        _uncraftableCardIds = uncraftableCardIds.ToHashSet(StringComparer.Ordinal);
        _uncraftableGoldenCardIds = uncraftableGoldenCardIds.ToHashSet(StringComparer.Ordinal);
    }

    public static Craftability Load(string? overrideDirectory = null)
    {
        var file = GameDataFiles.Read<CraftFile>(GameDataFiles.NonCraftableFile, overrideDirectory);
        return new Craftability(file.CraftableSets, file.NonCraftableSets, file.CraftableRarities, file.CraftableTypes,
            file.UncraftableCardIds, file.UncraftableGoldenCardIds);
    }

    public CraftabilityStatus StatusOf(CardDef card, PremiumKind kind = PremiumKind.Normal)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_nonCraftableSets.Contains(card.Set)
            || !_craftableRarities.Contains(card.Rarity)
            || !_craftableTypes.Contains(card.Type)
            || _uncraftableCardIds.Contains(card.CardId)
            || (kind == PremiumKind.Golden && _uncraftableGoldenCardIds.Contains(card.CardId)))
        {
            return CraftabilityStatus.NotCraftable;
        }

        return _craftableSets.Contains(card.Set) ? CraftabilityStatus.Craftable : CraftabilityStatus.Unknown;
    }

    public bool IsCraftable(CardDef card, PremiumKind kind = PremiumKind.Normal) =>
        StatusOf(card, kind) == CraftabilityStatus.Craftable;

    public bool IsSetKnown(string set) => _craftableSets.Contains(set) || _nonCraftableSets.Contains(set);

    private sealed class CraftFile
    {
        public List<string> CraftableSets { get; set; } = new();

        public List<string> NonCraftableSets { get; set; } = new();

        public List<string> CraftableRarities { get; set; } = new();

        public List<string> CraftableTypes { get; set; } = new();

        public List<string> UncraftableCardIds { get; set; } = new();

        public List<string> UncraftableGoldenCardIds { get; set; } = new();
    }
}
