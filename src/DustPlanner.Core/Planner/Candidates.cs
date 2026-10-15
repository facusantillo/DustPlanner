using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Planner;

/// <summary>Why a copy is or is not a candidate. Shown next to every recommendation.</summary>
public enum PainCategory
{
    Surplus,
    Refund,
    Blacklisted,
    Redundant,
    PremiumUnused,
    WildOnly,
    StandardNoSignal,
    StandardWithSignal,
}

/// <summary>A group of identical candidate copies: same card, same premium kind, same value and pain.</summary>
public sealed record CandidateGroup(
    CardDef Card,
    PremiumKind Kind,
    int Copies,
    int DustEach,
    int PainEach,
    PainCategory Category,
    IReadOnlyList<string> Reasons,
    bool IsStandard,
    bool RefundActive);

/// <summary>A card (or copies of it) kept out of the candidate pool, with the reason — reported so the user can audit the plan.</summary>
public sealed record ExcludedCopies(CardDef? Card, int DbfId, PremiumKind Kind, int Copies, string Reason, int? DustEach);

public sealed record CandidatePool(IReadOnlyList<CandidateGroup> Groups, IReadOnlyList<ExcludedCopies> Excluded)
{
    public int TotalDust => Groups.Sum(g => g.Copies * g.DustEach);
}

/// <summary>Turns a collection into scored disenchant candidates, applying hard protections first, then pain scoring.</summary>
public static class CandidateBuilder
{
    public static CandidatePool Build(
        CollectionSnapshot collection,
        CardCatalog catalog,
        GameRules rules,
        UserPrefs prefs,
        IReadOnlyList<ProtectedDeck> protectedDecks,
        MetaSignal meta,
        Refunds refunds,
        DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(protectedDecks);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(refunds);

        var groups = new List<CandidateGroup>();
        var excluded = new List<ExcludedCopies>();
        var rotating = rules.Rotation.RotatingSets(asOf);
        var whitelist = prefs.Whitelist.ToHashSet();
        var blacklist = prefs.Blacklist.ToHashSet();

        foreach (var owned in collection.Cards.OrderBy(c => c.DbfId))
        {
            if (!catalog.TryGet(owned.DbfId, out var card))
            {
                foreach (var (kind, copies) in owned.Copies)
                {
                    excluded.Add(new ExcludedCopies(null, owned.DbfId, kind, copies, "not in the card catalog", null));
                }

                continue;
            }

            var limit = rules.Dust.PlayableLimit(card.Rarity);
            var isStandard = rules.Rotation.IsStandard(card.Set, asOf);
            var usingDeck = protectedDecks.Count == 0 ? null : protectedDecks.MaxBy(d => d.CopiesOf(card.DbfId));
            var usedInDecks = usingDeck?.CopiesOf(card.DbfId) ?? 0;
            var metaProtected = IsMetaProtected(card, isStandard, prefs, meta);
            var protectedNeed = Math.Min(limit, Math.Max(usedInDecks, metaProtected || whitelist.Contains(card.DbfId) ? limit : 0));

            // 1. Surplus per premium kind: anything above the playable limit is always a free candidate (correction 4).
            var playable = new Dictionary<PremiumKind, int>();
            foreach (var (kind, copies) in owned.Copies)
            {
                var surplus = Math.Max(0, copies - limit);
                playable[kind] = copies - surplus;
                if (surplus > 0)
                {
                    AddGroup(groups, excluded, card, kind, surplus, PainCategory.Surplus, prefs.Pain.Surplus, rules, refunds, asOf, isStandard,
                        new[] { $"surplus: {copies} {KindName(kind)} copies, playable limit {limit}" });
                }
            }

            // 2. Protect `protectedNeed` playable copies, choosing which kinds to keep by the golden policy.
            var remaining = protectedNeed;
            foreach (var kind in KeepOrder(prefs.GoldenPolicy))
            {
                if (remaining == 0 || !playable.TryGetValue(kind, out var copies) || copies == 0)
                {
                    continue;
                }

                var keep = Math.Min(copies, remaining);
                playable[kind] = copies - keep;
                remaining -= keep;
                var reason = whitelist.Contains(card.DbfId) ? "whitelisted"
                    : usedInDecks >= keep ? $"in use ({usedInDecks} in \"{usingDeck!.Name}\")"
                    : "protected by meta signal";
                var protectedValue = rules.Craftability.IsCraftable(card, kind) ? DustValue(card, kind, rules, refunds, asOf) : null; // DustValue is already null for unknown kinds
                excluded.Add(new ExcludedCopies(card, card.DbfId, kind, keep, reason, protectedValue));
            }

            // 3. Score what is left.
            foreach (var (kind, copies) in playable.Where(p => p.Value > 0))
            {
                var reasons = new List<string>();
                PainCategory category;
                int pain;
                if (card.IsRewardCopy(kind) && !blacklist.Contains(card.DbfId))
                {
                    // Login / adventure rewards: the game never lets you craft them again and their disenchant value is
                    // not published anywhere we can check. Conservative default: excluded until the user opts in.
                    var how = kind == PremiumKind.Golden ? card.HowToEarnGolden : card.HowToEarn;
                    excluded.Add(new ExcludedCopies(card, card.DbfId, kind, copies,
                        $"reward copy that cannot be re-crafted ({how}); disenchant value to verify in game, add {card.DbfId} to prefs.json \"blacklist\" to allow", null));
                    continue;
                }

                if (blacklist.Contains(card.DbfId))
                {
                    category = PainCategory.Blacklisted;
                    pain = prefs.Pain.Blacklisted;
                    reasons.Add("blacklisted in prefs.json");
                }
                else if (protectedNeed > 0)
                {
                    category = PainCategory.Redundant;
                    pain = prefs.Pain.Redundant;
                    reasons.Add(usedInDecks > 0
                        ? $"redundant: {usedInDecks} copy(ies) in use are covered by another premium kind"
                        : "redundant: a protected copy of another premium kind is kept");
                }
                else if (kind != PremiumKind.Normal)
                {
                    category = PainCategory.PremiumUnused;
                    pain = isStandard ? prefs.Pain.PremiumUnusedStandard : prefs.Pain.PremiumUnusedWild;
                    reasons.Add($"{KindName(kind)} copy not used in any deck ({(isStandard ? "Standard" : "Wild")})");
                }
                else if (!isStandard)
                {
                    category = PainCategory.WildOnly;
                    pain = prefs.Pain.WildOnly + (card.IsLegendary ? prefs.Pain.WildOnlyLegendaryBonus : 0);
                    reasons.Add(prefs.IsStandardPlayer ? "Wild-only card and you play Standard" : "Wild-only card");
                }
                else
                {
                    var share = meta.Share(card.DbfId);
                    if (share > 0)
                    {
                        category = PainCategory.StandardWithSignal;
                        pain = prefs.Pain.StandardNoSignal + (int)Math.Round(prefs.Pain.StandardSignalWeight * share);
                        reasons.Add($"Standard card seen in {share:P0} of meta decks (below the protection threshold)");
                    }
                    else
                    {
                        category = PainCategory.StandardNoSignal;
                        pain = prefs.Pain.StandardNoSignal;
                        reasons.Add("Standard card, no meta signal");
                    }
                }

                if (category is not PainCategory.Surplus and not PainCategory.Blacklisted)
                {
                    if (!prefs.PlaysClass(card))
                    {
                        pain += isStandard ? prefs.Pain.ClassNotPlayedStandard : prefs.Pain.ClassNotPlayedWild;
                        reasons.Add($"class you do not play ({string.Join("/", card.Classes.Select(Capitalize))})");
                    }

                    if (isStandard && rotating.Contains(card.Set))
                    {
                        pain = Math.Max(0, pain - prefs.Pain.RotatingDiscount);
                        reasons.Add("rotates at the next rotation");
                    }
                }

                AddGroup(groups, excluded, card, kind, copies, category, Math.Clamp(pain, 0, 100), rules, refunds, asOf, isStandard, reasons);
            }
        }

        return new CandidatePool(groups, excluded);
    }

    private static void AddGroup(List<CandidateGroup> groups, List<ExcludedCopies> excluded, CardDef card, PremiumKind kind, int copies,
        PainCategory category, int pain, GameRules rules, Refunds refunds, DateOnly asOf, bool isStandard, IEnumerable<string> reasons)
    {
        if (kind == PremiumKind.Unknown)
        {
            excluded.Add(new ExcludedCopies(card, card.DbfId, kind, copies, "unknown premium kind (HDT slot 2/3 not confirmed)", null));
            return;
        }

        var craftability = rules.Craftability.StatusOf(card, kind);
        if (craftability != CraftabilityStatus.Craftable)
        {
            excluded.Add(new ExcludedCopies(card, card.DbfId, kind, copies,
                craftability == CraftabilityStatus.NotCraftable ? "not craftable (no dust value)" : $"unknown craftability (set {card.Set})", null));
            return;
        }

        var refund = refunds.Active(card.DbfId, kind, asOf);
        var value = refund is null ? rules.Dust.DisenchantValue(card.Rarity, kind) : rules.Dust.RefundValue(card.Rarity, kind);
        if (value is null || value <= 0)
        {
            excluded.Add(new ExcludedCopies(card, card.DbfId, kind, copies, $"dust value unknown for {KindName(kind)} copies", null));
            return;
        }

        var reasonList = reasons.ToList();
        if (refund is not null)
        {
            category = PainCategory.Refund;
            pain = Math.Min(pain, 0);
            reasonList.Insert(0, $"full refund until {refund.EndsAt:yyyy-MM-dd}{(refund.Note is null ? string.Empty : " (" + refund.Note + ")")}");
        }

        groups.Add(new CandidateGroup(card, kind, copies, value.Value, pain, category, reasonList, isStandard, refund is not null));
    }

    private static int? DustValue(CardDef card, PremiumKind kind, GameRules rules, Refunds refunds, DateOnly asOf) =>
        refunds.Active(card.DbfId, kind, asOf) is null ? rules.Dust.DisenchantValue(card.Rarity, kind) : rules.Dust.RefundValue(card.Rarity, kind);

    private static bool IsMetaProtected(CardDef card, bool isStandard, UserPrefs prefs, MetaSignal meta)
    {
        if (!isStandard || meta.DeckCount == 0)
        {
            return false;
        }

        var threshold = card.Rarity switch
        {
            Rarities.Legendary => prefs.MetaProtection.LegendaryShare,
            Rarities.Epic => prefs.MetaProtection.EpicShare,
            _ => double.PositiveInfinity,
        };
        if (double.IsPositiveInfinity(threshold))
        {
            return false;
        }

        if (meta.Share(card.DbfId) >= threshold)
        {
            return true;
        }

        return prefs.MetaProtection.ProtectPlayedClassDecks
               && meta.ClassesUsing(card.DbfId).Any(c => prefs.ClassesPlayed.Contains(c, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Order in which copies are kept when a card is in use.</summary>
    public static IReadOnlyList<PremiumKind> KeepOrder(GoldenPolicy policy) => policy == GoldenPolicy.KeepGolden
        ? new[] { PremiumKind.Diamond, PremiumKind.Signature, PremiumKind.Unknown, PremiumKind.Golden, PremiumKind.Normal }
        : new[] { PremiumKind.Normal, PremiumKind.Unknown, PremiumKind.Diamond, PremiumKind.Signature, PremiumKind.Golden };

    public static string KindName(PremiumKind kind) => kind switch
    {
        PremiumKind.Normal => "regular",
        PremiumKind.Golden => "golden",
        PremiumKind.Signature => "signature",
        PremiumKind.Diamond => "diamond",
        _ => "unknown-kind",
    };

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
}
