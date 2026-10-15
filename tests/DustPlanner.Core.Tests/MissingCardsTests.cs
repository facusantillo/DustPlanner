using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Tests;

public class MissingCardsTests
{
    /// <summary>
    /// Synthetic Priest deck (hero 1100): 1003×1, 1004×2, 1005×2, 1008×2, 1002×2, 1010×1, 1013×2, 9999×1,
    /// sideboard 1007×1 owned by 1003. Generated with python-hearthstone.
    /// </summary>
    public const string SyntheticDeck = "AAECAcwIA+sH8gePTgXqB+wH7QfwB/UHAAEB7wfrBwAA";

    private static MissingReport Analyze(string? collectionJson = null)
    {
        var rules = TestData.Rules();
        var catalog = TestData.SyntheticCatalog();
        var collection = HdtExportImporter.Import(collectionJson ?? TestData.Fixture("collection.synthetic.json"), rules.PremiumSlots);
        return MissingCards.Analyze(DeckstringCodec.Decode(SyntheticDeck), collection, catalog, rules, TestData.Today);
    }

    [Fact]
    public void Reports_missing_copies_costs_and_shortfall()
    {
        var report = Analyze();

        Assert.Equal("PRIEST", report.DeckClass);
        var missing = report.MissingCraftable.ToDictionary(m => m.Card.DbfId);
        Assert.Equal(1, missing[1003].Missing);
        Assert.Equal(1600, missing[1003].CraftCostMissing);
        Assert.Equal(1, missing[1004].Missing); // own 1 of 2
        Assert.Equal(400, missing[1004].CraftCostMissing);
        Assert.Equal(2, missing[1005].Missing);
        Assert.Equal(200, missing[1005].CraftCostMissing);
        Assert.Equal(1, missing[1007].Missing); // sideboard
        Assert.True(missing[1007].InSideboard);
        Assert.Equal(40, missing[1007].CraftCostMissing);
        Assert.DoesNotContain(1008, missing.Keys); // normal + golden = 2 copies
        Assert.DoesNotContain(1013, missing.Keys);

        Assert.Equal(2240, report.TotalCraftCost);
        Assert.Equal(640, report.DustAvailable);
        Assert.Equal(1600, report.Shortfall);
        Assert.False(report.Complete);
    }

    [Fact]
    public void Separates_not_craftable_unknown_craftability_and_unknown_cards()
    {
        var json = TestData.Fixture("collection.synthetic.json").Replace("\"1002\": [2, 0, 0, 0, 0, 0, 0, 0]", "\"1002\": [1, 0, 0, 0, 0, 0, 0, 0]", StringComparison.Ordinal);
        var report = Analyze(json);

        var notCraftable = report.MissingNotCraftable.Single(); // 1002 is CORE: one copy missing, cannot be crafted
        Assert.Equal(1002, notCraftable.Card.DbfId);
        Assert.Equal(1, notCraftable.Missing);
        Assert.Null(notCraftable.CraftCostMissing);
        Assert.Equal(2240, report.TotalCraftCost); // unchanged: Core copies never count
        Assert.Equal(1010, report.MissingUnknownCraftability.Single().Card.DbfId);
        Assert.Equal(9999, report.UnknownCards.Single().DbfId);
        Assert.Contains(report.Warnings, w => w.Contains("FUTURE_SET", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("not in the card catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void Fabled_companions_in_the_deck_are_included_with_their_card_not_missing_or_unknown()
    {
        // The game writes the two companion tokens of a Fabled legendary into the deck code (30 slots in total).
        var deck = DeckstringCodec.Decode(SyntheticDeck);
        var withFabled = deck with
        {
            Cards = deck.Cards.Concat(new[] { new DeckCard(1900, 1), new DeckCard(1901, 1), new DeckCard(1902, 1) }).ToList(),
        };
        var rules = TestData.Rules();
        var catalog = TestData.SyntheticCatalog();
        var collection = HdtExportImporter.Import(TestData.Fixture("collection.synthetic.json"), rules.PremiumSlots);

        var report = MissingCards.Analyze(DeckstringCodec.Decode(DeckstringCodec.Encode(withFabled)), collection, catalog, rules, TestData.Today);

        Assert.Equal(2, report.Companions.Count);
        Assert.All(report.Companions, c => Assert.Equal(1900, c.Parent.DbfId));
        Assert.Contains(report.Companions, c => c.Companion.CardId == "SYN_900t2" && c.Count == 1);
        Assert.Equal(9999, report.UnknownCards.Single().DbfId); // the companions are not "unknown ids"
        Assert.Contains(report.Missing, s => s.Card.DbfId == 1900 && s.Missing == 1); // the legendary itself is missing (1,600 dust)
        Assert.Equal(2240 + 1600, report.TotalCraftCost); // the companions add nothing
    }

    [Fact]
    public void Lists_rotating_cards_with_dust_at_risk()
    {
        var report = Analyze();

        Assert.Equal(new[] { 1003, 1004, 1013 }, report.Rotation.Cards.Select(c => c.Card.DbfId).Order().ToArray());
        Assert.Equal(2000, report.Rotation.DustAtRisk); // 1600 + 400; 1013 is owned
        Assert.Contains("2027-03", report.Rotation.NextRotation);
    }

    [Fact]
    public void Complete_deck_has_no_missing_lines()
    {
        var json = TestData.Fixture("collection.synthetic.json")
            .Replace("\"1004\": [1, 0, 0, 0, 0, 0, 0, 0]", "\"1004\": [2, 0, 0, 0, 0, 0, 0, 0], \"1003\": [1, 0, 0, 0, 0, 0, 0, 0], \"1005\": [0, 2, 0, 0, 0, 0, 0, 0], \"1007\": [1, 0, 0, 0, 0, 0, 0, 0], \"1010\": [1, 0, 0, 0, 0, 0, 0, 0], \"9999\": [1, 0, 0, 0, 0, 0, 0, 0]", StringComparison.Ordinal);

        var report = Analyze(json);

        Assert.Empty(report.Missing);
        Assert.Equal(0, report.TotalCraftCost);
        Assert.Equal(0, report.Shortfall);
        Assert.False(report.Complete); // 9999 is still unknown to the catalog
        Assert.Equal(0, report.Rotation.DustAtRisk);
    }

    [Fact]
    public void Standard_deck_with_wild_cards_is_flagged()
    {
        var rules = TestData.Rules();
        var catalog = TestData.SyntheticCatalog();
        var deck = new DeckCode(DeckFormat.Standard, [1100], [new DeckCard(1006, 1), new DeckCard(1007, 2)], []);
        var collection = new CollectionSnapshot("k", DateTimeOffset.UtcNow, "test", 0, []);

        var report = MissingCards.Analyze(deck, collection, catalog, rules, TestData.Today);

        Assert.Contains(report.Warnings, w => w.Contains("not Standard-legal", StringComparison.Ordinal) && w.Contains("Titan Relic", StringComparison.Ordinal));
        Assert.Equal(1600 + 80, report.TotalCraftCost);
    }
}
