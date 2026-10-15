using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace DustPlanner.Core.Tests;

public class DisenchantPlannerTests
{
    private static readonly GameRules Rules = TestData.Rules();
    private static readonly CardCatalog Catalog = TestData.SyntheticCatalog();

    private static PlanRequest Request(CollectionSnapshot collection, IReadOnlyList<ProtectedDeck>? decks = null, UserPrefs? prefs = null, int? need = null, bool allowNoDecks = false)
    {
        var deck = DeckstringCodec.Decode(MissingCardsTests.SyntheticDeck);
        var missing = MissingCards.Analyze(deck, collection, Catalog, Rules, TestData.Today);
        return new PlanRequest(deck, missing, collection, Catalog, Rules, prefs ?? new UserPrefs(), decks ?? Array.Empty<ProtectedDeck>(), MetaSignal.Empty, Refunds.Empty, TestData.Today)
        {
            NeedOverride = need,
            AllowNoDecks = allowNoDecks,
        };
    }

    private static CollectionSnapshot FixtureCollection() =>
        HdtExportImporter.Import(TestData.Fixture("collection.synthetic.json"), Rules.PremiumSlots);

    [Fact]
    public void Refuses_to_plan_without_saved_decks_unless_allowed()
    {
        var blocked = DisenchantPlanner.Plan(Request(FixtureCollection()));
        Assert.False(blocked.HasPlan);
        Assert.Contains(blocked.Blockers, b => b.Contains("No saved decks", StringComparison.Ordinal));
        Assert.StartsWith(DisenchantPlan.PacksWarning, blocked.Warnings[0], StringComparison.Ordinal);

        var allowed = DisenchantPlanner.Plan(Request(FixtureCollection(), allowNoDecks: true));
        Assert.True(allowed.HasPlan);
        Assert.Contains(allowed.Warnings, w => w.Contains("only the target deck is protected", StringComparison.Ordinal));
    }

    [Fact]
    public void Plans_the_exact_shortfall_with_the_least_painful_copies_and_protects_the_target_deck()
    {
        var decks = new[] { new ProtectedDeck("Aggro", "test", "1", new Dictionary<int, int> { [1014] = 2 }) };
        var plan = DisenchantPlanner.Plan(Request(FixtureCollection(), decks));

        Assert.True(plan.HasPlan);
        Assert.Equal(1600, plan.Need);
        var pick = Assert.Single(plan.Main!.Picks);
        Assert.Equal(1006, pick.Group.Card.DbfId); // golden Wild legendary, unused: 1600 dust for pain 3
        Assert.Equal(PremiumKind.Golden, pick.Group.Kind);
        Assert.Equal(1600, plan.Main.TotalDust);
        Assert.Equal(3, plan.Main.TotalPain);
        Assert.Equal(0, plan.Main.Surplus);

        // Target-deck cards are protected: 1008 (×2 in the deck, 1 regular + 1 golden owned) and 1013.
        Assert.Contains(plan.Pool.Excluded, e => e.DbfId == 1008 && e.Reason.Contains("in use", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Pool.Groups, g => g.Card.DbfId == 1008 || g.Card.DbfId == 1013);
        // Deck "Aggro" protects two copies of 1014; the golden ones are kept (keep-golden policy), surplus remains free.
        Assert.Contains(plan.Pool.Excluded, e => e.DbfId == 1014 && e.Kind == PremiumKind.Golden && e.Copies == 2);
        Assert.Empty(plan.Alternatives); // nothing else can reach 1600
        Assert.Contains(plan.NotableExclusions, e => e.DbfId == 1008);
    }

    [Fact]
    public void Offers_alternatives_when_other_covers_exist()
    {
        var collection = new CollectionSnapshot("k", DateTimeOffset.UtcNow, "test", 0, new[]
        {
            CollectionSnapshot.FromHdtCounts(1006, new[] { 0, 1, 0, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default), // golden Wild legendary 1600, pain 3
            CollectionSnapshot.FromHdtCounts(1003, new[] { 2, 0, 0, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default), // legendary: 1 surplus (400, pain 0) + 1 Standard (400, pain 25)
            CollectionSnapshot.FromHdtCounts(1004, new[] { 4, 0, 0, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default), // epic: 2 surplus (100 each)
            CollectionSnapshot.FromHdtCounts(1005, new[] { 4, 0, 0, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default), // rare Mage: 2 surplus (20 each) + 2 at pain 37
            CollectionSnapshot.FromHdtCounts(1012, new[] { 4, 0, 0, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default), // rare: 2 surplus + 2 at pain 34
        });

        // Target deck protects 1003×1, 1004×2, 1005×2 (surplus copies stay free). Need 700:
        //   main = golden Titan Relic (1600, pain 3, surplus 900) → objective 3 + 9.0 + 0.1
        //   alternative = all surplus (680, pain 0) + one Cataclysm Bomb (20, pain 34) = 700 exact
        var plan = DisenchantPlanner.Plan(Request(collection, allowNoDecks: true, need: 700));

        Assert.True(plan.HasPlan);
        Assert.Equal(1006, Assert.Single(plan.Main!.Picks).Group.Card.DbfId);
        var alternative = Assert.Single(plan.Alternatives);
        Assert.StartsWith("without Titan Relic (synthetic) (golden)", alternative.Label, StringComparison.Ordinal);
        Assert.Equal(700, alternative.Result.TotalDust);
        Assert.Equal(34, alternative.Result.TotalPain);
        Assert.All(plan.Alternatives, a => Assert.True(a.Result.Feasible && a.Result.TotalDust >= 700));
        Assert.All(plan.Alternatives, a => Assert.True(a.Result.Objective(new OptimizerSettings()) >= plan.Main.Objective(new OptimizerSettings())));
        Assert.All(plan.Alternatives, a => Assert.False(a.Result.Picks.SequenceEqual(plan.Main.Picks)));
    }

    [Fact]
    public void Blocks_when_nothing_is_needed_or_the_snapshot_is_stale()
    {
        var rich = new CollectionSnapshot("k", DateTimeOffset.UtcNow, "test", 99999, FixtureCollection().Cards);
        var nothing = DisenchantPlanner.Plan(Request(rich, allowNoDecks: true));
        Assert.False(nothing.HasPlan);
        Assert.Contains(nothing.Blockers, b => b.Contains("already have", StringComparison.Ordinal));

        var stale = new CollectionSnapshot("k", DateTimeOffset.UtcNow.AddDays(-45), "test", 0, FixtureCollection().Cards);
        var old = DisenchantPlanner.Plan(Request(stale, allowNoDecks: true));
        Assert.Contains(old.Blockers, b => b.Contains("days old", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocks_when_candidates_cannot_cover_the_need()
    {
        var poor = new CollectionSnapshot("k", DateTimeOffset.UtcNow, "test", 0, new[]
        {
            CollectionSnapshot.FromHdtCounts(1005, new[] { 2, 0, 0, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default),
        });

        var plan = DisenchantPlanner.Plan(Request(poor, allowNoDecks: true, need: 1000));

        Assert.False(plan.HasPlan);
        Assert.Contains(plan.Blockers, b => b.Contains("Not enough dust", StringComparison.Ordinal));
    }

    [Property(MaxTest = 100)]
    public Property Never_proposes_protected_copies_and_always_meets_the_need_or_explains(
        NonEmptyArray<byte> counts, NonEmptyArray<byte> deckCounts, PositiveInt needUnits)
    {
        var cards = Catalog.All.OrderBy(c => c.DbfId).ToList();
        var owned = cards.Select((c, i) => CollectionSnapshot.FromHdtCounts(c.DbfId,
            new[] { counts.Get[i % counts.Get.Length] % 4, counts.Get[(i + 1) % counts.Get.Length] % 3, 0, 0, 0, 0, 0, 0 }, PremiumSlotMap.Default)).ToList();
        var collection = new CollectionSnapshot("k", DateTimeOffset.UtcNow, "test", 0, owned);
        var deck = new ProtectedDeck("Saved", "test", "1", cards.Select((c, i) => (c.DbfId, Count: deckCounts.Get[i % deckCounts.Get.Length] % 3))
            .Where(x => x.Count > 0).ToDictionary(x => x.DbfId, x => x.Count));
        var need = 5 * Math.Min(needUnits.Get, 1000);

        var plan = DisenchantPlanner.Plan(Request(collection, new[] { deck }, need: need));
        var target = ProtectedDeck.FromDeckCode(DeckstringCodec.Decode(MissingCardsTests.SyntheticDeck), "t", "target", "t");

        var protectedOk = true;
        if (plan.Main is { Feasible: true } main)
        {
            foreach (var card in cards)
            {
                var limit = Rules.Dust.PlayableLimit(card.Rarity);
                var required = Math.Min(limit, Math.Max(deck.CopiesOf(card.DbfId), target.CopiesOf(card.DbfId)));
                var picked = main.Picks.Where(p => p.Group.Card.DbfId == card.DbfId).Sum(p => p.Copies);
                var left = collection.TotalCopies(card.DbfId) - picked;
                if (left < Math.Min(required, collection.TotalCopies(card.DbfId)))
                {
                    protectedOk = false;
                }
            }
        }

        var meetsOrExplains = plan.HasPlan ? plan.Main!.TotalDust >= need : plan.Blockers.Count > 0;
        var packsFirst = plan.Warnings[0].StartsWith(DisenchantPlan.PacksWarning, StringComparison.Ordinal);
        var alternativesDiffer = plan.Alternatives.All(a => !a.Result.Picks.SequenceEqual(plan.Main!.Picks) && a.Result.Feasible);
        return protectedOk.Label("protected copies untouched")
            .And(meetsOrExplains.Label("meets need or explains"))
            .And(packsFirst.Label("packs warning first"))
            .And(alternativesDiffer.Label("alternatives differ and are feasible"));
    }
}
