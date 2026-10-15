using DustPlanner.Core.Cards;

namespace DustPlanner.Core.Rules;

/// <summary>Craft and disenchant values per rarity and premium kind, from <c>data/dust-rules.json</c>. <c>null</c> = unknown.</summary>
public sealed class DustRules
{
    private readonly Dictionary<(string Rarity, PremiumKind Kind), int?> _craft;
    private readonly Dictionary<(string Rarity, PremiumKind Kind), int?> _disenchant;
    private readonly Dictionary<string, int> _playableLimit;

    public DustRules(
        IReadOnlyDictionary<(string Rarity, PremiumKind Kind), int?> craft,
        IReadOnlyDictionary<(string Rarity, PremiumKind Kind), int?> disenchant,
        IReadOnlyDictionary<string, int> playableLimit,
        int defaultPlayableLimit)
    {
        _craft = new Dictionary<(string, PremiumKind), int?>(craft);
        _disenchant = new Dictionary<(string, PremiumKind), int?>(disenchant);
        _playableLimit = new Dictionary<string, int>(playableLimit, StringComparer.Ordinal);
        DefaultPlayableLimit = defaultPlayableLimit;
    }

    public int DefaultPlayableLimit { get; }

    /// <summary>Dust needed to craft one copy, or <c>null</c> when unknown for that premium kind.</summary>
    public int? CraftCost(string rarity, PremiumKind kind) =>
        _craft.TryGetValue((rarity, kind), out var value) ? value : null;

    /// <summary>Dust returned by disenchanting one copy, or <c>null</c> when unknown.</summary>
    public int? DisenchantValue(string rarity, PremiumKind kind) =>
        _disenchant.TryGetValue((rarity, kind), out var value) ? value : null;

    /// <summary>Full refund for a nerfed card: the craft cost of the same premium kind (assumption, see docs/VERIFICACION.md C3).</summary>
    public int? RefundValue(string rarity, PremiumKind kind) => CraftCost(rarity, kind);

    /// <summary>Maximum playable copies: 1 for legendaries, 2 otherwise.</summary>
    public int PlayableLimit(string rarity) =>
        _playableLimit.TryGetValue(rarity, out var limit) ? limit : DefaultPlayableLimit;

    public static DustRules Load(string? overrideDirectory = null)
    {
        var file = GameDataFiles.Read<DustFile>(GameDataFiles.DustRulesFile, overrideDirectory);
        return new DustRules(Flatten(file.Craft), Flatten(file.Disenchant),
            file.PlayableLimit.Where(p => p.Key != "default").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            file.PlayableLimit.GetValueOrDefault("default", 2));
    }

    private static Dictionary<(string, PremiumKind), int?> Flatten(Dictionary<string, Dictionary<string, int?>> table)
    {
        var result = new Dictionary<(string, PremiumKind), int?>();
        foreach (var (rarity, kinds) in table)
        {
            foreach (var (kindName, value) in kinds)
            {
                if (Enum.TryParse<PremiumKind>(kindName, ignoreCase: true, out var kind))
                {
                    result[(rarity, kind)] = value;
                }
            }
        }

        return result;
    }

    private sealed class DustFile
    {
        public Dictionary<string, Dictionary<string, int?>> Craft { get; set; } = new();

        public Dictionary<string, Dictionary<string, int?>> Disenchant { get; set; } = new();

        public Dictionary<string, int> PlayableLimit { get; set; } = new();
    }
}
