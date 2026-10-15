using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Json;

namespace DustPlanner.Core.Rules;

public enum CraftabilityStatus
{
    /// <summary>Set, rarity and type are all known to be craftable, and the copy is not a reward copy.</summary>
    Craftable,

    /// <summary>Known not to be craftable nor disenchantable (Core set, free rarity, reward copy, listed id, ...).</summary>
    NotCraftable,

    /// <summary>The set is in neither list; treated as not craftable for dust purposes, and reported.</summary>
    Unknown,
}

/// <summary>
/// Craftability rules from <c>data/non-craftable.json</c>, merged with the user's partial override of the same name
/// under the home <c>data</c> folder. "Uncraftable" in Hearthstone means the copy can be neither crafted nor
/// disenchanted, so a <see cref="CraftabilityStatus.NotCraftable"/> copy never has a dust value.
/// </summary>
public sealed class Craftability
{
    private readonly HashSet<string> _craftableSets;
    private readonly HashSet<string> _nonCraftableSets;
    private readonly HashSet<string> _nonCraftableSetsRegular;
    private readonly HashSet<string> _craftableRarities;
    private readonly HashSet<string> _craftableTypes;
    private readonly HashSet<string> _uncraftableIds;
    private readonly HashSet<string> _uncraftableGoldenIds;
    private readonly HashSet<string> _craftableIds;
    private readonly HashSet<string> _craftableGoldenIds;
    private readonly List<Regex> _craftableHowToEarn;

    public Craftability(
        IEnumerable<string> craftableSets,
        IEnumerable<string> nonCraftableSets,
        IEnumerable<string> craftableRarities,
        IEnumerable<string> craftableTypes,
        IEnumerable<string> uncraftableCardIds,
        IEnumerable<string> uncraftableGoldenCardIds)
        : this(craftableSets, nonCraftableSets, Array.Empty<string>(), craftableRarities, craftableTypes, uncraftableCardIds, uncraftableGoldenCardIds,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
    {
    }

    public Craftability(
        IEnumerable<string> craftableSets,
        IEnumerable<string> nonCraftableSets,
        IEnumerable<string> nonCraftableSetsRegular,
        IEnumerable<string> craftableRarities,
        IEnumerable<string> craftableTypes,
        IEnumerable<string> uncraftableCardIds,
        IEnumerable<string> uncraftableGoldenCardIds,
        IEnumerable<string> craftableCardIds,
        IEnumerable<string> craftableGoldenCardIds,
        IEnumerable<string> howToEarnCraftablePatterns)
    {
        _craftableSets = craftableSets.ToHashSet(StringComparer.Ordinal);
        _nonCraftableSets = nonCraftableSets.ToHashSet(StringComparer.Ordinal);
        _nonCraftableSetsRegular = nonCraftableSetsRegular.ToHashSet(StringComparer.Ordinal);
        _craftableRarities = craftableRarities.ToHashSet(StringComparer.Ordinal);
        _craftableTypes = craftableTypes.ToHashSet(StringComparer.Ordinal);
        _uncraftableIds = uncraftableCardIds.ToHashSet(StringComparer.Ordinal);
        _uncraftableGoldenIds = uncraftableGoldenCardIds.ToHashSet(StringComparer.Ordinal);
        _craftableIds = craftableCardIds.ToHashSet(StringComparer.Ordinal);
        _craftableGoldenIds = craftableGoldenCardIds.ToHashSet(StringComparer.Ordinal);
        _craftableHowToEarn = howToEarnCraftablePatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
    }

    /// <summary>Embedded defaults merged with the user's partial override file (lists are unioned).</summary>
    public static Craftability Load(string? overrideDirectory = null)
    {
        var file = JsonSerializer.Deserialize<CraftFile>(GameDataFiles.ReadText(GameDataFiles.NonCraftableFile), CoreJson.Options)
            ?? throw new InvalidDataException("Rule file is empty: " + GameDataFiles.NonCraftableFile);
        var overridePath = overrideDirectory is null ? null : Path.Combine(overrideDirectory, GameDataFiles.NonCraftableFile);
        if (overridePath is not null && File.Exists(overridePath))
        {
            var user = JsonSerializer.Deserialize<CraftFile>(File.ReadAllText(overridePath), CoreJson.Options);
            if (user is not null)
            {
                file = file.MergedWith(user);
            }
        }

        return new Craftability(file.CraftableSets, file.NonCraftableSets, file.NonCraftableSetsRegular, file.CraftableRarities, file.CraftableTypes,
            file.UncraftableCardIds.Select(Key), file.UncraftableGoldenCardIds.Select(Key), file.CraftableCardIds.Select(Key), file.CraftableGoldenCardIds.Select(Key),
            file.HowToEarnCraftablePatterns);
    }

    public CraftabilityStatus StatusOf(CardDef card, PremiumKind kind = PremiumKind.Normal) => Explain(card, kind).Status;

    /// <summary>Status plus the reason the game would show ("Core Set cards cannot be disenchanted or crafted", the unlock text of a reward copy, ...).</summary>
    public (CraftabilityStatus Status, string? Reason) Explain(CardDef card, PremiumKind kind = PremiumKind.Normal)
    {
        ArgumentNullException.ThrowIfNull(card);
        var idKey = Key(card.DbfId);
        var golden = kind == PremiumKind.Golden;

        // 1. Explicit per-card overrides (the user saw the game's verdict).
        var listedUncraftable = golden
            ? _uncraftableGoldenIds.Contains(idKey) || _uncraftableGoldenIds.Contains(card.CardId)
            : _uncraftableIds.Contains(idKey) || _uncraftableIds.Contains(card.CardId);
        if (listedUncraftable)
        {
            return (CraftabilityStatus.NotCraftable, "listed as uncraftable in non-craftable.json");
        }

        var forcedCraftable = golden
            ? _craftableGoldenIds.Contains(idKey) || _craftableGoldenIds.Contains(card.CardId)
            : _craftableIds.Contains(idKey) || _craftableIds.Contains(card.CardId);

        // 2. Set, rarity, type.
        if (_nonCraftableSets.Contains(card.Set))
        {
            return (CraftabilityStatus.NotCraftable, $"{SetLabel(card.Set)} cards cannot be disenchanted or crafted");
        }

        if (kind == PremiumKind.Normal && _nonCraftableSetsRegular.Contains(card.Set))
        {
            return (CraftabilityStatus.NotCraftable, $"regular {SetLabel(card.Set)} cards cannot be disenchanted or crafted");
        }

        if (!_craftableRarities.Contains(card.Rarity))
        {
            return (CraftabilityStatus.NotCraftable, $"{(card.Rarity.Length == 0 ? "no" : card.Rarity.ToLowerInvariant())} rarity: cannot be disenchanted or crafted");
        }

        if (!_craftableTypes.Contains(card.Type))
        {
            return (CraftabilityStatus.NotCraftable, $"{card.Type.ToLowerInvariant()} cards cannot be disenchanted or crafted");
        }

        // 3. Reward copies (game data: howToEarn / howToEarnGolden), unless the user overrode the card as craftable.
        if (!forcedCraftable && kind is PremiumKind.Normal or PremiumKind.Golden)
        {
            var how = golden ? card.HowToEarnGolden : card.HowToEarn;
            if (!string.IsNullOrWhiteSpace(how) && !_craftableHowToEarn.Any(r => r.IsMatch(how)))
            {
                return (CraftabilityStatus.NotCraftable, $"reward copy, cannot be disenchanted or crafted ({StripTags(how)})");
            }
        }

        return _craftableSets.Contains(card.Set)
            ? (CraftabilityStatus.Craftable, null)
            : (CraftabilityStatus.Unknown, $"set {card.Set} is not in data/non-craftable.json");
    }

    public bool IsCraftable(CardDef card, PremiumKind kind = PremiumKind.Normal) =>
        StatusOf(card, kind) == CraftabilityStatus.Craftable;

    public bool IsSetKnown(string set) => _craftableSets.Contains(set) || _nonCraftableSets.Contains(set);

    /// <summary>True when every copy of every card in <paramref name="set"/> is uncraftable (Core, Classic format, events, ...).</summary>
    public bool IsSetNonCraftable(string set) => _nonCraftableSets.Contains(set);

    /// <summary>Adds a per-card override to the user's partial file (created when missing) and returns its path.</summary>
    public static string WriteOverride(string overrideDirectory, int dbfId, PremiumKind kind, bool uncraftable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overrideDirectory);
        Directory.CreateDirectory(overrideDirectory);
        var path = Path.Combine(overrideDirectory, GameDataFiles.NonCraftableFile);
        var file = File.Exists(path) ? JsonSerializer.Deserialize<CraftFile>(File.ReadAllText(path), CoreJson.Options) ?? new CraftFile() : new CraftFile();
        var key = Key(dbfId);
        var golden = kind == PremiumKind.Golden;
        var add = golden ? (uncraftable ? file.UncraftableGoldenCardIds : file.CraftableGoldenCardIds) : (uncraftable ? file.UncraftableCardIds : file.CraftableCardIds);
        var remove = golden ? (uncraftable ? file.CraftableGoldenCardIds : file.UncraftableGoldenCardIds) : (uncraftable ? file.CraftableCardIds : file.UncraftableCardIds);
        remove.RemoveAll(k => Key(k) == key);
        if (!add.Any(k => Key(k) == key))
        {
            add.Add(key);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(file, CoreJson.Options));
        return path;
    }

    /// <summary>Removes every per-card override for the copy (the shipped rule applies again). Returns false when there was none.</summary>
    public static bool ClearOverride(string overrideDirectory, int dbfId, PremiumKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overrideDirectory);
        var path = Path.Combine(overrideDirectory, GameDataFiles.NonCraftableFile);
        if (!File.Exists(path))
        {
            return false;
        }

        var file = JsonSerializer.Deserialize<CraftFile>(File.ReadAllText(path), CoreJson.Options) ?? new CraftFile();
        var key = Key(dbfId);
        var lists = kind == PremiumKind.Golden
            ? new[] { file.UncraftableGoldenCardIds, file.CraftableGoldenCardIds }
            : new[] { file.UncraftableCardIds, file.CraftableCardIds };
        var removed = lists.Sum(l => l.RemoveAll(k => Key(k) == key));
        if (removed > 0)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(file, CoreJson.Options));
        }

        return removed > 0;
    }

    /// <summary>The user's per-card overrides: (dbfId or card id, kind, uncraftable) for each entry of the partial file.</summary>
    public static IReadOnlyList<(string Card, PremiumKind Kind, bool Uncraftable)> ReadOverrides(string overrideDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overrideDirectory);
        var path = Path.Combine(overrideDirectory, GameDataFiles.NonCraftableFile);
        if (!File.Exists(path))
        {
            return Array.Empty<(string, PremiumKind, bool)>();
        }

        var file = JsonSerializer.Deserialize<CraftFile>(File.ReadAllText(path), CoreJson.Options) ?? new CraftFile();
        var result = new List<(string, PremiumKind, bool)>();
        result.AddRange(file.UncraftableCardIds.Select(k => (Key(k), PremiumKind.Normal, true)));
        result.AddRange(file.UncraftableGoldenCardIds.Select(k => (Key(k), PremiumKind.Golden, true)));
        result.AddRange(file.CraftableCardIds.Select(k => (Key(k), PremiumKind.Normal, false)));
        result.AddRange(file.CraftableGoldenCardIds.Select(k => (Key(k), PremiumKind.Golden, false)));
        return result;
    }

    private static string Key(object value) => value switch
    {
        int i => i.ToString(CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetInt32().ToString(CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static string SetLabel(string set) => set switch
    {
        "CORE" => "Core Set",
        "CORE_HIDDEN" => "former Core Set",
        "EVENT" => "event",
        "VANILLA" => "Classic format",
        "HERO_SKINS" => "hero skin",
        "DEMON_HUNTER_INITIATE" => "Demon Hunter Initiate",
        _ => set,
    };

    private static string StripTags(string text) => Regex.Replace(text, "<[^>]+>", string.Empty).Trim();

    /// <summary>On-disk shape; ids may be numbers or strings. Every list is optional so a partial user file merges cleanly.</summary>
    private sealed class CraftFile
    {
        public List<string> CraftableSets { get; set; } = new();

        public List<string> NonCraftableSets { get; set; } = new();

        public List<string> NonCraftableSetsRegular { get; set; } = new();

        public List<string> CraftableRarities { get; set; } = new();

        public List<string> CraftableTypes { get; set; } = new();

        public List<string> HowToEarnCraftablePatterns { get; set; } = new();

        public List<object> UncraftableCardIds { get; set; } = new();

        public List<object> UncraftableGoldenCardIds { get; set; } = new();

        public List<object> CraftableCardIds { get; set; } = new();

        public List<object> CraftableGoldenCardIds { get; set; } = new();

        public CraftFile MergedWith(CraftFile user) => new()
        {
            CraftableSets = Union(CraftableSets, user.CraftableSets),
            NonCraftableSets = Union(NonCraftableSets, user.NonCraftableSets),
            NonCraftableSetsRegular = Union(NonCraftableSetsRegular, user.NonCraftableSetsRegular),
            CraftableRarities = user.CraftableRarities.Count > 0 ? user.CraftableRarities : CraftableRarities,
            CraftableTypes = user.CraftableTypes.Count > 0 ? user.CraftableTypes : CraftableTypes,
            HowToEarnCraftablePatterns = Union(HowToEarnCraftablePatterns, user.HowToEarnCraftablePatterns),
            UncraftableCardIds = UncraftableCardIds.Concat(user.UncraftableCardIds).ToList(),
            UncraftableGoldenCardIds = UncraftableGoldenCardIds.Concat(user.UncraftableGoldenCardIds).ToList(),
            CraftableCardIds = CraftableCardIds.Concat(user.CraftableCardIds).ToList(),
            CraftableGoldenCardIds = CraftableGoldenCardIds.Concat(user.CraftableGoldenCardIds).ToList(),
        };

        private static List<string> Union(List<string> a, List<string> b) => a.Concat(b).Distinct(StringComparer.Ordinal).ToList();
    }
}
