using DustPlanner.Contracts;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;
using DustPlanner.Store;

namespace DustPlanner.Store.Tests;

public class StoreTests
{
    private const string SyntheticDeck = "AAECAcwIA+sH8gePTgXqB+wH7QfwB/UHAAEB7wfrBwAA";
    private static readonly GameRules Rules = GameDataFiles.LoadRules();
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static CardCatalog Catalog() => HearthstoneJsonParser.Parse(Fixture("cards.synthetic.json"), "synthetic", "enUS");

    private static CollectionSnapshot Collection(string? replace = null, string? with = null, DateTimeOffset? takenAt = null)
    {
        var json = Fixture("collection.synthetic.json");
        if (replace is not null)
        {
            json = json.Replace(replace, with!, StringComparison.Ordinal);
        }

        var c = HdtExportImporter.Import(json, Rules.PremiumSlots);
        return takenAt is null ? c : new CollectionSnapshot(c.AccountKey, takenAt.Value, c.Source, c.Dust, c.Cards, c.Gold, c.UnopenedPacks, c.BattleTag, c.Region);
    }

    [Fact]
    public void Migrations_apply_once_and_file_database_uses_wal()
    {
        using var memory = DustStore.OpenInMemory();
        Assert.Equal(1, memory.SchemaVersion);

        var path = Path.Combine(Path.GetTempPath(), "dustplanner-store-" + Guid.NewGuid().ToString("N"), "test.db");
        try
        {
            using (var store = DustStore.Open(path))
            {
                Assert.Equal(1, store.SchemaVersion);
                Assert.Equal("wal", (string)store.Scalar("PRAGMA journal_mode")!);
            }

            using (var again = DustStore.Open(path))
            {
                Assert.Equal(1, again.SchemaVersion);
            }

            using var reader = DustStore.Open(path, readOnly: true);
            Assert.Empty(new SnapshotRepository(reader).Accounts());
        }
        finally
        {
            TempDir.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Snapshot_import_deduplicates_by_content_and_round_trips()
    {
        using var store = DustStore.OpenInMemory();
        var repo = new SnapshotRepository(store);
        var collection = Collection();

        var (first, isNew) = repo.Import(collection);
        var (second, isNewAgain) = repo.Import(collection);

        Assert.True(isNew);
        Assert.False(isNewAgain);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(["8589934592-42"], repo.Accounts());

        var loaded = repo.Load(first.Id);
        Assert.Equal(collection.Dust, loaded.Dust);
        Assert.Equal(collection.BattleTag, loaded.BattleTag);
        Assert.Equal(collection.DistinctCards, loaded.DistinctCards);
        Assert.Equal(1, loaded.Copies(1012, PremiumKind.Unknown));
        Assert.Equal(4, loaded.Copies(1014, PremiumKind.Golden));
        Assert.Equal(SnapshotRepository.ContentHash(collection), SnapshotRepository.ContentHash(loaded));
    }

    [Fact]
    public void Decks_import_upserts_and_feeds_the_planner()
    {
        using var store = DustStore.OpenInMemory();
        var decks = new DeckRepository(store);
        var export = new DecksExport
        {
            Meta = new ExportMeta { Kind = ExportSchema.DecksKind, ExportedAt = DateTimeOffset.UtcNow, Account = new AccountRef { Hi = 8589934592, Lo = 42 } },
            Decks =
            {
                new DeckExport { Origin = DeckOrigins.InGame, ExternalId = "1", Name = "Priest", CardClass = "Priest", FormatType = 2, Cards = { new DeckCardExport { DbfId = 1003, Count = 1 }, new DeckCardExport { DbfId = 1013, Count = 2 } } },
                new DeckExport { Origin = DeckOrigins.Hdt, ExternalId = "g", Name = "Old", Archived = true, Cards = { new DeckCardExport { DbfId = 1004, Count = 2 } } },
            },
        };
        new SnapshotRepository(store).UpsertAccount("8589934592-42", "Synthetic#0001", "EU", DateTimeOffset.UtcNow);

        Assert.Equal(2, decks.ImportExport(export));
        export.Decks[0].Name = "Priest v2";
        Assert.Equal(2, decks.ImportExport(export));

        var listed = decks.List("8589934592-42");
        Assert.Single(listed); // archived deck hidden
        Assert.Equal("Priest v2", listed[0].Name);
        Assert.Equal(2, decks.List("8589934592-42", includeArchived: true).Count);

        var prefs = new UserPrefs();
        var protectedDecks = decks.ProtectedDecks("8589934592-42", prefs);
        Assert.Single(protectedDecks);
        Assert.Equal(2, protectedDecks[0].CopiesOf(1013));

        var target = DeckstringCodec.Decode(SyntheticDeck);
        var id = decks.SaveDeckCode("8589934592-42", target, "target", "t1", "Target", "PRIEST", DateTimeOffset.UtcNow);
        Assert.True(id > 0);
        Assert.Single(decks.ProtectedDecks("8589934592-42", prefs)); // targets are not "saved decks"
    }

    [Fact]
    public void Sync_records_disenchants_matched_to_a_plan_and_unplanned_changes()
    {
        using var store = DustStore.OpenInMemory();
        var catalog = Catalog();
        var history = new HistoryService(store);
        var plans = new PlanRepository(store);
        var t0 = new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);

        // 1. First snapshot, then a plan proposing the golden Titan Relic (1006).
        var first = history.Sync(Collection(takenAt: t0), catalog, Rules, Refunds.Empty, Today, t0);
        Assert.True(first.NewSnapshot);
        Assert.Empty(first.Events);

        var deck = DeckstringCodec.Decode(SyntheticDeck);
        var collection = Collection(takenAt: t0);
        var missing = MissingCards.Analyze(deck, collection, catalog, Rules, Today);
        var plan = DisenchantPlanner.Plan(new PlanRequest(deck, missing, collection, catalog, Rules, new UserPrefs(), Array.Empty<ProtectedDeck>(), MetaSignal.Empty, Refunds.Empty, Today) { AllowNoDecks = true });
        Assert.True(plan.HasPlan);
        var planId = plans.Save(collection.AccountKey, deck, plan, new { note = "test" }, t0);
        Assert.Equal(PlanStatus.Proposed, plans.List(collection.AccountKey)[0].Status);

        // 2. Next snapshot: golden 1006 gone (as planned), one regular 1014 gone (not planned), two 1005 gained, dust up.
        var t1 = t0.AddHours(2);
        var json = Fixture("collection.synthetic.json")
            .Replace("\"1006\": [0, 1, 0, 0, 0, 0, 0, 0]", "\"1006\": [0, 0, 0, 0, 0, 0, 0, 0]", StringComparison.Ordinal)
            .Replace("\"1014\": [2, 4, 0, 0, 0, 0, 0, 0]", "\"1014\": [1, 4, 0, 0, 0, 0, 0, 0], \"1005\": [2, 0, 0, 0, 0, 0, 0, 0]", StringComparison.Ordinal)
            .Replace("\"dust\": 640", "\"dust\": 2245", StringComparison.Ordinal);
        var next = HdtExportImporter.Import(json, Rules.PremiumSlots);
        next = new CollectionSnapshot(next.AccountKey, t1, next.Source, next.Dust, next.Cards, next.Gold, null, next.BattleTag, next.Region);

        var result = history.Sync(next, catalog, Rules, Refunds.Empty, Today, t1);

        Assert.True(result.NewSnapshot);
        Assert.Equal(1605, result.DustDelta);
        Assert.Equal(3, result.Events.Count);

        var planned = result.Events.Single(e => e.DbfId == 1006);
        Assert.Equal(EventKinds.Disenchanted, planned.EventKind);
        Assert.Equal(PremiumKind.Golden, planned.Kind);
        Assert.Equal(-1, planned.Delta);
        Assert.Equal(planId, planned.PlanId);
        Assert.Equal(Confidence.High, planned.ConfidenceLevel);
        Assert.Equal(1600, planned.DustEstimate);

        var unplanned = result.Events.Single(e => e.DbfId == 1014);
        Assert.Null(unplanned.PlanId);
        Assert.Equal(Confidence.Low, unplanned.ConfidenceLevel);
        Assert.Equal(5, unplanned.DustEstimate); // common, regular

        var gained = result.Events.Single(e => e.DbfId == 1005);
        Assert.Equal(EventKinds.Gained, gained.EventKind);
        Assert.Equal(2, gained.Delta);

        Assert.Equal(PlanStatus.Confirmed, plans.List(collection.AccountKey)[0].Status);
        Assert.Equal(3, history.Events(collection.AccountKey).Count);

        // 3. Re-importing the same snapshot is a no-op.
        var again = history.Sync(next, catalog, Rules, Refunds.Empty, Today, t1.AddMinutes(5));
        Assert.False(again.NewSnapshot);
        Assert.Empty(again.Events);
    }

    [Fact]
    public void Rank_import_keeps_latest_per_format_and_describes_it()
    {
        using var store = DustStore.OpenInMemory();
        new SnapshotRepository(store).UpsertAccount("1-2", null, null, DateTimeOffset.UtcNow);
        var ranks = new RankRepository(store);
        var export = new RankExport
        {
            Meta = new ExportMeta { ExportedAt = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero), Account = new AccountRef { Hi = 1, Lo = 2 } },
            Formats = { [RankFormats.Standard] = new RankInfo { LeagueId = 5, StarLevel = 7, Stars = 3, SeasonId = 150, SeasonGames = 40, SeasonWins = 22 }, [RankFormats.Wild] = new RankInfo { LeagueId = 2, StarLevel = 1, LegendRank = 0 } },
        };

        Assert.Equal(2, ranks.Import(export));
        Assert.Equal(0, ranks.Import(export)); // same timestamp → ignored

        var standard = ranks.Latest("1-2", RankFormats.Standard)!;
        Assert.Equal("Diamond 4 (3 stars)", RankRepository.Describe(standard));
        Assert.Equal("Legend #42", RankRepository.Describe(standard with { LegendRank = 42 }));
        Assert.Equal("Silver 10 (0 stars)", RankRepository.Describe(ranks.Latest("1-2", RankFormats.Wild)!));
        Assert.Null(ranks.Latest("1-2", RankFormats.Twist));
    }
}
