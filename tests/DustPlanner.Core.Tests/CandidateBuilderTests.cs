using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Tests;

public class CandidateBuilderTests
{
    private readonly GameRules _rules = TestData.Rules();
    private readonly CardCatalog _catalog = TestData.SyntheticCatalog();

    private static CollectionSnapshot Collection(params (int DbfId, int Normal, int Golden, int Slot2)[] cards) =>
        new("k", DateTimeOffset.UtcNow, "test", 0,
            cards.Select(c => CollectionSnapshot.FromHdtCounts(c.DbfId, new[] { c.Normal, c.Golden, c.Slot2, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default)));

    private CandidatePool Build(CollectionSnapshot collection, UserPrefs? prefs = null, IReadOnlyList<ProtectedDeck>? decks = null, MetaSignal? meta = null, Refunds? refunds = null) =>
        CandidateBuilder.Build(collection, _catalog, _rules, prefs ?? new UserPrefs(), decks ?? Array.Empty<ProtectedDeck>(), meta ?? MetaSignal.Empty, refunds ?? Refunds.Empty, TestData.Today);

    private static ProtectedDeck Deck(string name, params (int DbfId, int Count)[] cards) =>
        new(name, "test", name, cards.ToDictionary(c => c.DbfId, c => c.Count));

    [Fact]
    public void Four_golden_commons_two_are_surplus_not_golden_unused()
    {
        // 1014: common Priest card from BE (Standard). 4 golden + 2 regular, not in any deck.
        var pool = Build(Collection((1014, 2, 4, 0)));

        var golden = pool.Groups.Where(g => g.Card.DbfId == 1014 && g.Kind == PremiumKind.Golden).ToList();
        var surplus = golden.Single(g => g.Category == PainCategory.Surplus);
        Assert.Equal(2, surplus.Copies);
        Assert.Equal(0, surplus.PainEach);
        Assert.Equal(50, surplus.DustEach);

        var unused = golden.Single(g => g.Category == PainCategory.PremiumUnused);
        Assert.Equal(2, unused.Copies);
        Assert.Equal(10, unused.PainEach); // Standard premium unused

        var regular = pool.Groups.Single(g => g.Card.DbfId == 1014 && g.Kind == PremiumKind.Normal);
        Assert.Equal(2, regular.Copies);
        Assert.Equal(PainCategory.StandardNoSignal, regular.Category);
        Assert.Equal(25, regular.PainEach);
    }

    [Fact]
    public void Copies_in_use_are_protected_and_the_golden_policy_decides_which_kind_is_kept()
    {
        // 1008: epic Priest/Mage (BE). 1 regular + 1 golden; a saved deck runs 1 copy.
        var decks = new[] { Deck("Priest", (1008, 1)) };

        var keep = Build(Collection((1008, 1, 1, 0)), new UserPrefs { GoldenPolicy = GoldenPolicy.KeepGolden }, decks);
        var keptGolden = keep.Excluded.Single(e => e.DbfId == 1008);
        Assert.Equal(PremiumKind.Golden, keptGolden.Kind);
        Assert.Contains("in use", keptGolden.Reason, StringComparison.Ordinal);
        var redundantRegular = keep.Groups.Single(g => g.Card.DbfId == 1008);
        Assert.Equal(PremiumKind.Normal, redundantRegular.Kind);
        Assert.Equal(PainCategory.Redundant, redundantRegular.Category);
        Assert.Equal(1, redundantRegular.PainEach);

        var dust = Build(Collection((1008, 1, 1, 0)), new UserPrefs { GoldenPolicy = GoldenPolicy.DustGolden }, decks);
        Assert.Equal(PremiumKind.Normal, dust.Excluded.Single(e => e.DbfId == 1008).Kind);
        Assert.Equal(PremiumKind.Golden, dust.Groups.Single(g => g.Card.DbfId == 1008).Kind);
    }

    [Fact]
    public void Deck_protection_never_exceeds_the_playable_limit_and_takes_the_max_over_decks()
    {
        // 1003: legendary. 2 regular copies; decks use 1 → 1 surplus (pain 0), 1 protected, nothing else.
        var pool = Build(Collection((1003, 2, 0, 0)), decks: new[] { Deck("A", (1003, 1)), Deck("B", (1003, 1)) });

        var surplus = pool.Groups.Single(g => g.Card.DbfId == 1003);
        Assert.Equal(PainCategory.Surplus, surplus.Category);
        Assert.Equal(1, surplus.Copies);
        Assert.Equal(400, surplus.DustEach);
        Assert.Equal(1, pool.Excluded.Single(e => e.DbfId == 1003).Copies);

        // 1004: epic, 2 copies; deck A uses 2, deck B uses 1 → both protected.
        var pool2 = Build(Collection((1004, 2, 0, 0)), decks: new[] { Deck("A", (1004, 2)), Deck("B", (1004, 1)) });
        Assert.DoesNotContain(pool2.Groups, g => g.Card.DbfId == 1004);
        Assert.Equal(2, pool2.Excluded.Single(e => e.DbfId == 1004).Copies);
    }

    [Fact]
    public void Unknown_kind_core_and_unknown_set_copies_are_excluded_with_reasons()
    {
        var pool = Build(Collection((1012, 0, 0, 1), (1002, 2, 0, 0), (1010, 1, 0, 0), (1001, 1, 0, 0)));

        Assert.Empty(pool.Groups);
        Assert.Contains(pool.Excluded, e => e.DbfId == 1012 && e.Reason.Contains("unknown premium kind", StringComparison.Ordinal));
        Assert.Contains(pool.Excluded, e => e.DbfId == 1002 && e.Reason.Contains("not craftable", StringComparison.Ordinal));
        Assert.Contains(pool.Excluded, e => e.DbfId == 1010 && e.Reason.Contains("unknown craftability", StringComparison.Ordinal));
        Assert.Contains(pool.Excluded, e => e.DbfId == 1001 && e.Reason.Contains("not craftable", StringComparison.Ordinal));
    }

    [Fact]
    public void Meta_signal_protects_standard_legendaries_and_epics_by_threshold_or_played_class()
    {
        // 10 meta decks: 1003 (legendary) in 2 of them (20% ≥ 10%); 1004 (epic, neutral) in 1 Mage deck (10% < 20%, class not played);
        // 1008 (epic Priest/Mage) in 1 Priest deck (10% < 20% but played class).
        var decks = new List<DeckCode>();
        for (var i = 0; i < 10; i++)
        {
            var cards = new List<DeckCard> { new(1013, 2) };
            if (i < 2)
            {
                cards.Add(new DeckCard(1003, 1));
            }

            if (i == 5)
            {
                cards.Add(new DeckCard(1004, 2));
                cards.Add(new DeckCard(1005, 2)); // Mage card → deck class Mage (no hero in deck)
            }

            if (i == 7)
            {
                cards.Add(new DeckCard(1008, 2));
            }

            decks.Add(new DeckCode(DeckFormat.Standard, i == 7 ? [1100] : [], cards, []));
        }

        var meta = MetaSignal.FromDecks(decks, _catalog);
        Assert.Equal(0.2, meta.Share(1003), 3);
        Assert.Contains("MAGE", meta.ClassesUsing(1004));
        Assert.Contains("PRIEST", meta.ClassesUsing(1008));

        var pool = Build(Collection((1003, 1, 0, 0), (1004, 2, 0, 0), (1008, 2, 0, 0), (1013, 2, 0, 0)), meta: meta);

        Assert.Contains(pool.Excluded, e => e.DbfId == 1003 && e.Reason == "protected by meta signal");
        Assert.Contains(pool.Excluded, e => e.DbfId == 1008 && e.Reason == "protected by meta signal" && e.Copies == 2);
        Assert.Contains(pool.Groups, g => g.Card.DbfId == 1004); // below threshold, class not played → candidate
        var common = pool.Groups.Single(g => g.Card.DbfId == 1013);
        Assert.Equal(PainCategory.StandardWithSignal, common.Category); // 100% share but commons are never meta-protected
        Assert.Equal(25 + 50 - 3, common.PainEach); // signal 1.0 → +50, TIME_TRAVEL rotates → −3
    }

    [Fact]
    public void Active_refund_pays_the_craft_cost_with_zero_pain()
    {
        var refunds = new Refunds(new[]
        {
            new RefundEntry { DbfId = 1003, Premium = "normal", StartsAt = new DateOnly(2026, 10, 10), EndsAt = new DateOnly(2026, 10, 24), Note = "nerf" },
            new RefundEntry { DbfId = 1004, Premium = "all", StartsAt = new DateOnly(2026, 1, 1), EndsAt = new DateOnly(2026, 1, 14) },
        });

        var pool = Build(Collection((1003, 1, 0, 0), (1004, 1, 1, 0)), refunds: refunds);

        var legendary = pool.Groups.Single(g => g.Card.DbfId == 1003);
        Assert.Equal(PainCategory.Refund, legendary.Category);
        Assert.Equal(1600, legendary.DustEach);
        Assert.Equal(0, legendary.PainEach);
        Assert.True(legendary.RefundActive);
        Assert.StartsWith("full refund until 2026-10-24 (nerf)", legendary.Reasons[0], StringComparison.Ordinal);

        Assert.All(pool.Groups.Where(g => g.Card.DbfId == 1004), g => Assert.False(g.RefundActive)); // window expired
    }

    [Fact]
    public void Modifiers_class_not_played_wild_and_rotation_apply()
    {
        // 1005: rare Mage (Standard, ESCAPEFROM_VIOLET_HOLD, not rotating): 25 + 12 = 37.
        // 1006: legendary neutral Wild (TITANS): 2 + 3 = 5.
        // 1007: common Rogue Wild: 2 + 5 = 7.
        // 1012: rare Warlock CATACLYSM (rotating): 25 + 12 − 3 = 34.
        var pool = Build(Collection((1005, 1, 0, 0), (1006, 1, 0, 0), (1007, 1, 0, 0), (1012, 1, 0, 0)));

        Assert.Equal(37, pool.Groups.Single(g => g.Card.DbfId == 1005).PainEach);
        Assert.Equal(5, pool.Groups.Single(g => g.Card.DbfId == 1006).PainEach);
        Assert.Equal(7, pool.Groups.Single(g => g.Card.DbfId == 1007).PainEach);
        Assert.Equal(34, pool.Groups.Single(g => g.Card.DbfId == 1012).PainEach);
        Assert.Contains("rotates at the next rotation", pool.Groups.Single(g => g.Card.DbfId == 1012).Reasons);
    }

    [Fact]
    public void Whitelist_excludes_and_blacklist_zeroes_pain_but_keeps_deck_protection()
    {
        var prefs = new UserPrefs { Whitelist = { 1005 }, Blacklist = { 1004 } };
        var pool = Build(Collection((1005, 2, 0, 0), (1004, 2, 0, 0)), prefs, new[] { Deck("A", (1004, 1)) });

        Assert.Contains(pool.Excluded, e => e.DbfId == 1005 && e.Reason == "whitelisted" && e.Copies == 2);
        Assert.DoesNotContain(pool.Groups, g => g.Card.DbfId == 1005);
        var black = pool.Groups.Single(g => g.Card.DbfId == 1004);
        Assert.Equal(PainCategory.Blacklisted, black.Category);
        Assert.Equal(0, black.PainEach);
        Assert.Equal(1, black.Copies); // one copy stays protected by deck A
    }
}
