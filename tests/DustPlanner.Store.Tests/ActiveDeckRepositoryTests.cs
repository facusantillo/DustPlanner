using DustPlanner.Contracts;
using DustPlanner.Store;

namespace DustPlanner.Store.Tests;

public class ActiveDeckRepositoryTests
{
    private const string Account = "8589934592-42";
    private const string Priest = "AAECAa0GAA==";
    private static readonly DateTimeOffset GameStart = new(2026, 10, 18, 20, 31, 0, TimeSpan.Zero);

    private static ActiveDeckExport Export(DateTimeOffset at, string trigger = "game_start", string? deckId = "6f0c", string name = "Priest Control", string cls = "PRIEST", string? format = "standard", bool noDeck = false) => new()
    {
        Meta = new ExportMeta { Kind = ExportSchema.ActiveDeckKind, ExportedAt = at, Trigger = trigger, Account = new AccountRef { Hi = 8589934592, Lo = 42, BattleTag = "Synthetic#0001", Region = "EU" } },
        Deck = noDeck ? null : new ActiveDeck { Name = name, Deckstring = Priest, HdtDeckId = deckId, CardClass = cls, Format = format },
    };

    private static void InsertGame(DustStore store, string key, DateTimeOffset startedAt, string? playerClass) =>
        store.Execute(
            "INSERT INTO game(account_key, game_key, started_at, player_class, result, source) VALUES ($k, $g, $s, $c, 'win', 'powerlog')",
            ("$k", Account), ("$g", key), ("$s", DustStore.Iso(startedAt)), ("$c", playerClass));

    private static long? DeckOf(DustStore store, string key) => store.Scalar("SELECT deck_id FROM game WHERE game_key = $g", ("$g", key)) as long?;

    [Fact]
    public void Import_stores_one_sample_per_export_and_ignores_null_decks_and_duplicates()
    {
        using var store = DustStore.OpenInMemory();
        var repo = new ActiveDeckRepository(store);

        Assert.Equal(1, repo.Import(Export(GameStart)));
        Assert.Equal(0, repo.Import(Export(GameStart)));                      // same taken_at → ignored
        Assert.Equal(0, repo.Import(Export(GameStart.AddMinutes(1), trigger: "mode_changed:HUB", noDeck: true)));
        Assert.Equal(1, repo.Import(Export(GameStart.AddMinutes(2), trigger: "active_deck_changed", deckId: "ab12", name: "Zoo", cls: "WARLOCK")));

        var latest = repo.Latest(Account);
        Assert.NotNull(latest);
        Assert.Equal("Zoo", latest.Name);
        Assert.Equal("WARLOCK", latest.CardClass);
        Assert.Equal("active_deck_changed", latest.Reason);
        Assert.Equal(2, repo.List(Account).Count);
        // The account row exists so that later rank/collection imports do not trip the foreign key.
        Assert.Equal(1L, store.Scalar("SELECT COUNT(*) FROM account WHERE account_key = $k", ("$k", Account)));
    }

    [Fact]
    public void Samples_older_than_the_retention_are_pruned_on_import()
    {
        using var store = DustStore.OpenInMemory();
        var repo = new ActiveDeckRepository(store);
        repo.Import(Export(GameStart.AddDays(-40)));
        repo.Import(Export(GameStart.AddDays(-20)));

        repo.Import(Export(GameStart));

        var kept = repo.List(Account).Select(s => s.TakenAt).ToList();
        Assert.Equal(new[] { GameStart, GameStart.AddDays(-20) }, kept);
    }

    [Fact]
    public void A_game_is_linked_to_the_sample_taken_around_its_start_with_the_same_class()
    {
        using var store = DustStore.OpenInMemory();
        var repo = new ActiveDeckRepository(store);
        repo.Import(Export(GameStart.AddMinutes(-3)));                                 // deck picked, then queued
        repo.Import(Export(GameStart.AddMinutes(-30), deckId: "old1", name: "Old"));   // too long ago
        InsertGame(store, "g1", GameStart, "PRIEST");

        var deckId = repo.LinkGame(Account, "g1", GameStart, "PRIEST");

        Assert.NotNull(deckId);
        Assert.Equal(deckId, DeckOf(store, "g1"));
        // The deck row was created from the sample with the HDT GUID as external id, so the decks export upserts the same row later.
        Assert.Equal("Priest Control", store.Scalar("SELECT name FROM deck WHERE id = $d", ("$d", deckId)));
        Assert.Equal("6f0c", store.Scalar("SELECT external_id FROM deck WHERE id = $d AND origin = 'hdt'", ("$d", deckId)));
        Assert.Equal(2L, store.Scalar("SELECT format_type FROM deck WHERE id = $d", ("$d", deckId)));
        Assert.Equal(Priest, store.Scalar("SELECT deckstring FROM deck WHERE id = $d", ("$d", deckId)));

        // Linking again (or another game with the same deck) reuses the row.
        InsertGame(store, "g2", GameStart.AddMinutes(1), "PRIEST");
        Assert.Equal(deckId, repo.LinkGame(Account, "g2", GameStart.AddMinutes(1), "PRIEST"));
        Assert.Equal(1L, store.Scalar("SELECT COUNT(*) FROM deck"));
    }

    [Fact]
    public void No_link_when_the_class_differs_is_unknown_or_the_sample_is_outside_the_window()
    {
        using var store = DustStore.OpenInMemory();
        var repo = new ActiveDeckRepository(store);
        repo.Import(Export(GameStart.AddSeconds(30)));   // written on OnGameStart, a few seconds after the log line

        InsertGame(store, "mage", GameStart, "MAGE");
        Assert.Null(repo.LinkGame(Account, "mage", GameStart, "MAGE"));

        InsertGame(store, "unknown", GameStart, null);
        Assert.Null(repo.LinkGame(Account, "unknown", GameStart, null));

        InsertGame(store, "late", GameStart.AddMinutes(11), "PRIEST");
        Assert.Null(repo.LinkGame(Account, "late", GameStart.AddMinutes(11), "PRIEST"));

        InsertGame(store, "early", GameStart.AddMinutes(-2), "PRIEST");          // sample 2.5 min after the start: beyond 60 s
        Assert.Null(repo.LinkGame(Account, "early", GameStart.AddMinutes(-2), "PRIEST"));

        InsertGame(store, "ok", GameStart, "PRIEST");
        Assert.NotNull(repo.LinkGame(Account, "ok", GameStart, "PRIEST"));
        Assert.Equal(0L, store.Scalar("SELECT COUNT(*) FROM game WHERE game_key <> 'ok' AND deck_id IS NOT NULL"));
    }

    [Fact]
    public void An_export_imported_after_the_game_links_the_games_it_covers()
    {
        using var store = DustStore.OpenInMemory();
        var repo = new ActiveDeckRepository(store);
        InsertGame(store, "g1", GameStart, "PRIEST");
        InsertGame(store, "g2", GameStart.AddMinutes(5), "PRIEST");       // next game, same deck
        InsertGame(store, "g3", GameStart.AddMinutes(20), "PRIEST");      // beyond the window of this sample
        Assert.Null(DeckOf(store, "g1"));

        repo.Import(Export(GameStart.AddSeconds(5)));

        Assert.NotNull(DeckOf(store, "g1"));
        Assert.Equal(DeckOf(store, "g1"), DeckOf(store, "g2"));
        Assert.Null(DeckOf(store, "g3"));
    }

    [Fact]
    public void The_newest_sample_in_the_window_wins()
    {
        using var store = DustStore.OpenInMemory();
        var repo = new ActiveDeckRepository(store);
        repo.Import(Export(GameStart.AddMinutes(-5), deckId: "first", name: "First Priest"));
        repo.Import(Export(GameStart.AddMinutes(-1), deckId: "second", name: "Second Priest"));
        InsertGame(store, "g1", GameStart, "PRIEST");

        var deckId = repo.LinkGame(Account, "g1", GameStart, "PRIEST");

        Assert.Equal("Second Priest", store.Scalar("SELECT name FROM deck WHERE id = $d", ("$d", deckId)));
    }
}
