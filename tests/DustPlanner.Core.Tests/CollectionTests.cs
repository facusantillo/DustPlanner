using DustPlanner.Core.Collection;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Tests;

public class CollectionTests
{
    private readonly GameRules _rules = TestData.Rules();

    [Fact]
    public void Hdt_export_maps_slots_through_the_premium_map()
    {
        var collection = HdtExportImporter.Import(TestData.Fixture("collection.synthetic.json"), _rules.PremiumSlots);

        Assert.Equal("8589934592-42", collection.AccountKey);
        Assert.Equal("Synthetic#0001", collection.BattleTag);
        Assert.Equal("EU", collection.Region);
        Assert.Equal(CollectionSources.Hdt, collection.Source);
        Assert.Equal(640, collection.Dust);
        Assert.Equal(1200, collection.Gold);
        Assert.Null(collection.UnopenedPacks);
        Assert.Equal(8, collection.DistinctCards);
        Assert.Equal(1, collection.Copies(1008, PremiumKind.Normal));
        Assert.Equal(1, collection.Copies(1008, PremiumKind.Golden));
        Assert.Equal(2, collection.TotalCopies(1008));
        Assert.Equal(1, collection.Copies(1012, PremiumKind.Unknown)); // slot 2 unconfirmed
        Assert.Equal(0, collection.Get(1012)!.KnownCopies);
        Assert.Equal(6, collection.TotalCopies(1014));
        Assert.Equal(0, collection.TotalCopies(4242));
    }

    [Fact]
    public void Hdt_export_with_a_newer_schema_is_rejected()
    {
        var json = TestData.Fixture("collection.synthetic.json").Replace("\"schema_version\": 1", "\"schema_version\": 99", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => HdtExportImporter.Import(json, _rules.PremiumSlots));
    }

    [Fact]
    public void Hsreplay_json_accepts_short_and_long_arrays()
    {
        const string json = """
            { "collection": { "1002": [2, 0], "1004": [1, 1, 0], "1008": [1, 1, 1, 0], "1013": [2, 0, 0, 0, 1, 1, 1, 1], "x": [1] }, "dust": 900, "cardbacks": [1] }
            """;

        var collection = HsReplayImporter.Import(json, _rules.PremiumSlots, "hsreplay-test");

        Assert.Equal(CollectionSources.HsReplay, collection.Source);
        Assert.Equal(900, collection.Dust);
        Assert.Equal(4, collection.DistinctCards);
        Assert.Equal(2, collection.TotalCopies(1004));
        Assert.Equal(1, collection.Copies(1008, PremiumKind.Unknown));
        Assert.Equal(2, collection.TotalCopies(1013)); // trial slots ignored
        Assert.Throws<InvalidDataException>(() => HsReplayImporter.Import("{}", _rules.PremiumSlots));
    }

    [Fact]
    public void Csv_resolves_ids_and_names_and_rejects_ambiguity()
    {
        var catalog = TestData.SyntheticCatalog();
        const string csv = """
            # my cards
            dbf_id,name,normal,golden
            1003,,1,0
            ,Lost City Scout (synthetic),2,0
            ,dual caster (SYNTHETIC),0,1
            """;

        var collection = CsvCollectionImporter.Import(csv, catalog, dust: 100);

        Assert.Equal(CollectionSources.Csv, collection.Source);
        Assert.Equal(100, collection.Dust);
        Assert.Equal(1, collection.TotalCopies(1003));
        Assert.Equal(2, collection.TotalCopies(1004));
        Assert.Equal(1, collection.Copies(1008, PremiumKind.Golden));

        Assert.Throws<InvalidDataException>(() => CsvCollectionImporter.Import("name;normal\nNobody;1", catalog, 0));
        Assert.Throws<InvalidDataException>(() => CsvCollectionImporter.Import("golden\n1", catalog, 0));
    }

    [Fact]
    public void Inbox_lists_accounts_and_selects_by_key_or_battletag()
    {
        var inbox = Path.Combine(Path.GetTempPath(), "dustplanner-inbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(inbox, "8589934592-42");
            var b = Path.Combine(inbox, "1-2");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "collection.json"), TestData.Fixture("collection.synthetic.json"));
            File.WriteAllText(Path.Combine(a, "decks.json"), "{}");
            File.WriteAllText(Path.Combine(b, "collection.json"), TestData.Fixture("collection.synthetic.json")
                .Replace("Synthetic#0001", "Other#0002", StringComparison.Ordinal)
                .Replace("2026-10-14T20:10:00+02:00", "2026-10-15T09:00:00+02:00", StringComparison.Ordinal));

            var accounts = InboxLocator.ListAccounts(inbox);

            Assert.Equal(2, accounts.Count);
            Assert.Equal("1-2", accounts[0].AccountKey); // most recent first
            Assert.True(accounts[1].HasDecks);
            Assert.False(accounts[0].HasDecks);

            Assert.Equal("8589934592-42", InboxLocator.Select(accounts, "synthetic#0001").Account!.AccountKey);
            Assert.Equal("1-2", InboxLocator.Select(accounts, "1-2").Account!.AccountKey);
            Assert.Equal("1-2", InboxLocator.Select(accounts, null).Account!.AccountKey);
            Assert.Null(InboxLocator.Select(accounts, null, allowMostRecent: false).Account);
            Assert.Contains("not found", InboxLocator.Select(accounts, "nope").Reason);
            Assert.Empty(InboxLocator.ListAccounts(Path.Combine(inbox, "missing")));
        }
        finally
        {
            Directory.Delete(inbox, recursive: true);
        }
    }
}
