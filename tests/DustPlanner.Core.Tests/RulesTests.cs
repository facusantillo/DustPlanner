using DustPlanner.Core.Cards;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Tests;

public class RulesTests
{
    private readonly GameRules _rules = TestData.Rules();

    [Fact]
    public void Current_year_is_scarab_and_standard_sets_match_the_data_file()
    {
        var year = _rules.Rotation.YearOn(TestData.Today);

        Assert.Equal("SCARAB", year.Name);
        Assert.True(_rules.Rotation.IsStandard("CORE", TestData.Today));
        Assert.True(_rules.Rotation.IsStandard("BE", TestData.Today));
        Assert.True(_rules.Rotation.IsStandard("EMERALD_DREAM", TestData.Today));
        Assert.False(_rules.Rotation.IsStandard("TITANS", TestData.Today));
        Assert.False(_rules.Rotation.IsStandard("WHIZBANGS_WORKSHOP", TestData.Today));
        Assert.False(_rules.Rotation.IsStandard("LEGACY", TestData.Today));
    }

    [Fact]
    public void Rotating_sets_are_those_shared_with_the_previous_year_except_core_and_event()
    {
        var rotating = _rules.Rotation.RotatingSets(TestData.Today);

        Assert.Equal(new[] { "CATACLYSM", "EMERALD_DREAM", "THE_LOST_CITY", "TIME_TRAVEL" }, rotating.Order().ToArray());
        Assert.DoesNotContain("CORE", rotating);
        Assert.DoesNotContain("BE", rotating);
    }

    [Fact]
    public void Year_boundaries_use_the_rotation_date()
    {
        Assert.Equal("RAPTOR", _rules.Rotation.YearOn(new DateOnly(2026, 3, 16)).Name);
        Assert.Equal("SCARAB", _rules.Rotation.YearOn(new DateOnly(2026, 3, 17)).Name);
        Assert.True(_rules.Rotation.IsStandard("WHIZBANGS_WORKSHOP", new DateOnly(2026, 3, 16)));
        Assert.Contains("estimated", _rules.Rotation.Next.Display);
    }

    [Fact]
    public void Dust_values_match_the_in_game_table_and_special_premiums_are_unknown()
    {
        var dust = _rules.Dust;

        Assert.Equal(40, dust.CraftCost(Rarities.Common, PremiumKind.Normal));
        Assert.Equal(1600, dust.CraftCost(Rarities.Legendary, PremiumKind.Normal));
        Assert.Equal(3200, dust.CraftCost(Rarities.Legendary, PremiumKind.Golden));
        Assert.Equal(5, dust.DisenchantValue(Rarities.Common, PremiumKind.Normal));
        Assert.Equal(1600, dust.DisenchantValue(Rarities.Legendary, PremiumKind.Golden));
        Assert.Equal(400, dust.DisenchantValue(Rarities.Epic, PremiumKind.Golden));
        Assert.Null(dust.DisenchantValue(Rarities.Epic, PremiumKind.Signature));
        Assert.Null(dust.DisenchantValue(Rarities.Epic, PremiumKind.Diamond));
        Assert.Null(dust.DisenchantValue(Rarities.Epic, PremiumKind.Unknown));
        Assert.Null(dust.DisenchantValue(Rarities.Free, PremiumKind.Normal));
        Assert.Equal(1600, dust.RefundValue(Rarities.Legendary, PremiumKind.Normal));
        Assert.Equal(1, dust.PlayableLimit(Rarities.Legendary));
        Assert.Equal(2, dust.PlayableLimit(Rarities.Epic));
    }

    [Fact]
    public void Premium_slots_default_to_unknown_for_indexes_2_and_3()
    {
        var slots = _rules.PremiumSlots;

        Assert.False(slots.Confirmed);
        Assert.Equal(PremiumKind.Normal, slots.KindOf(0));
        Assert.Equal(PremiumKind.Golden, slots.KindOf(1));
        Assert.Equal(PremiumKind.Unknown, slots.KindOf(2));
        Assert.Equal(PremiumKind.Unknown, slots.KindOf(3));
        Assert.Equal(PremiumKind.Unknown, slots.KindOf(9));
        Assert.Equal(1, slots.SlotOf(PremiumKind.Golden));
        Assert.Null(slots.SlotOf(PremiumKind.Signature));
    }

    [Fact]
    public void Craftability_distinguishes_core_free_unknown_and_craftable()
    {
        var catalog = TestData.SyntheticCatalog();
        var craft = _rules.Craftability;

        Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(catalog.GetByDbfId(1001)!)); // CORE + FREE
        Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(catalog.GetByDbfId(1002)!)); // CORE + COMMON
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(catalog.GetByDbfId(1003)!));
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(catalog.GetByDbfId(1008)!)); // BE, added by hand
        Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(catalog.GetByDbfId(1009)!)); // LEGACY + FREE
        Assert.Equal(CraftabilityStatus.Unknown, craft.StatusOf(catalog.GetByDbfId(1010)!)); // FUTURE_SET
        Assert.False(craft.IsSetKnown("FUTURE_SET"));
        Assert.True(craft.IsSetKnown("CORE"));
    }

    [Fact]
    public void Reward_copies_are_uncraftable_but_crafting_and_adventure_texts_are_not()
    {
        var craft = _rules.Craftability;
        CardDef Card(string set, string? how, string? howGolden) => new()
        {
            DbfId = 1, CardId = "T_1", Name = "t", Set = set, Rarity = "LEGENDARY", CardClass = "NEUTRAL", Classes = new[] { "NEUTRAL" }, Type = "MINION", Cost = 1,
            HowToEarn = how, HowToEarnGolden = howGolden,
        };

        // What the game showed on 2026-10-15: Marin the Fox (regular) and golden SN1P-SN4P are "Uncraftable".
        var marin = Card("LOOTAPALOOZA", "Unlocked by opening a Kobolds & Catacombs pack.", "Unlocked with the BlizzCon 2017 virtual ticket.");
        Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(marin));
        Assert.Contains("Kobolds", craft.Explain(marin).Reason, StringComparison.Ordinal);
        Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(marin, PremiumKind.Golden));

        var snip = Card("BOOMSDAY", null, "Log in to Hearthstone from June 3, 2019 to July 1, 2019.");
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(snip)); // the regular copy is an ordinary card
        Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(snip, PremiumKind.Golden));

        var freeLegendary = Card("EMERALD_DREAM", "Earnable after opening an <i>Into the Emerald Dream</i> card pack.", null);
        var (status, reason) = craft.Explain(freeLegendary);
        Assert.Equal(CraftabilityStatus.NotCraftable, status);
        Assert.DoesNotContain("<i>", reason, StringComparison.Ordinal);

        // Texts that describe crafting itself, or the pre-2020 adventure unlocks (craftable since then), are not rewards.
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(Card("LEGACY", "Not available in packs, must be crafted.", null)));
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(Card("KARA", "Unlocked in the Menagerie, in One Night in Karazhan.", "Crafting unlocked in the Menagerie, in One Night in Karazhan."), PremiumKind.Golden));
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(Card("LOE", "Unlocked by starting the League of Explorers adventure.", null)));
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(Card("NAXX", "Unlocked in The Arachnid Quarter, in the Naxxramas adventure.", null)));

        // Demon Hunter Initiate: regular uncraftable, golden craftable.
        var initiate = Card("DEMON_HUNTER_INITIATE", "Unlocked with the Demon Hunter class.", "Obtained by crafting.");
        Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(initiate));
        Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(initiate, PremiumKind.Golden));
    }

    [Fact]
    public void User_overrides_merge_with_the_shipped_lists_and_block_or_allow_single_copies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dustplanner-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = TestData.SyntheticCatalog();
            var path = Craftability.WriteOverride(dir, 1003, PremiumKind.Golden, uncraftable: true);   // "the game said Uncraftable"
            Craftability.WriteOverride(dir, 1950, PremiumKind.Golden, uncraftable: false);            // user disagrees with the reward rule
            Assert.Contains("uncraftable_golden_card_ids", File.ReadAllText(path), StringComparison.Ordinal);

            var craft = Craftability.Load(dir);
            Assert.Equal(CraftabilityStatus.NotCraftable, craft.StatusOf(catalog.GetByDbfId(1003)!, PremiumKind.Golden));
            Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(catalog.GetByDbfId(1003)!)); // regular untouched
            Assert.Equal(CraftabilityStatus.Craftable, craft.StatusOf(catalog.GetByDbfId(1950)!, PremiumKind.Golden));
            Assert.True(craft.IsSetKnown("BE")); // shipped lists survive the merge

            Craftability.WriteOverride(dir, 1003, PremiumKind.Golden, uncraftable: false); // flipping removes the other entry
            Assert.Equal(CraftabilityStatus.Craftable, Craftability.Load(dir).StatusOf(catalog.GetByDbfId(1003)!, PremiumKind.Golden));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void Override_directory_takes_precedence_over_embedded_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dustplanner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, GameDataFiles.PremiumSlotsFile),
                """{ "confirmed": true, "slots": { "0": "normal", "1": "golden", "2": "signature", "3": "diamond" } }""");

            var slots = PremiumSlotMap.Load(dir);
            var rotation = FormatRotation.Load(dir); // not overridden → embedded

            Assert.True(slots.Confirmed);
            Assert.Equal(PremiumKind.Signature, slots.KindOf(2));
            Assert.Equal(PremiumKind.Diamond, slots.KindOf(3));
            Assert.Equal("SCARAB", rotation.YearOn(TestData.Today).Name);
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}
