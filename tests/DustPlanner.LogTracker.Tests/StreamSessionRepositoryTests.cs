using DustPlanner.LogTracker;
using DustPlanner.Store;

namespace DustPlanner.LogTracker.Tests;

/// <summary>Kick stage K7: sessions open and close by time, and their record and decks come from the games inside the window.</summary>
public sealed class StreamSessionRepositoryTests
{
    private const string Account = "8589934592-42";
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Open_is_idempotent_per_start_and_a_new_start_closes_the_previous_session()
    {
        using var store = DustStore.OpenInMemory();
        var sessions = new StreamSessionRepository(store);

        var first = sessions.Open(Account, "tsukuru-tv", T0, "Legend push", new SessionRank("Diamond 5", "standard", 5, 6, 0));
        Assert.Equal(first, sessions.Open(Account, "tsukuru-tv", T0, "Legend push (renamed)", null));
        var open = sessions.LatestOpen(Account);
        Assert.NotNull(open);
        Assert.Equal(first, open.Id);
        Assert.Equal("Legend push (renamed)", open.Title);
        Assert.True(open.IsLive);
        Assert.Equal("Diamond 5", open.RankStart!.Label);

        // The relay reports a new stream while the previous one was never closed: the old one ends where the new one starts.
        var second = sessions.Open(Account, "tsukuru-tv", T0.AddHours(5), null, null);
        Assert.NotEqual(first, second);
        var closed = sessions.Get(first)!;
        Assert.Equal(T0.AddHours(5), closed.EndedAt);
        Assert.Equal(second, sessions.LatestOpen(Account)!.Id);
        Assert.Equal(second, sessions.LatestOpen(null)!.Id);          // whichever account opened it (the overlay may have moved on)
        Assert.Null(sessions.LatestOpen("4294967296-7"));

        Assert.True(sessions.Close(second, T0.AddHours(7), new SessionRank("Diamond 2", "standard", 5, 9, 0)));
        Assert.False(sessions.Close(second, T0.AddHours(8), null)); // already closed: the repeat is ignored
        Assert.Equal("Diamond 2", sessions.Get(second)!.RankEnd!.Label);
        Assert.Null(sessions.LatestOpen(Account));
        Assert.Equal(new[] { second, first }, sessions.List(Account).Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Record_and_decks_come_from_the_games_started_inside_the_window()
    {
        using var store = DustStore.OpenInMemory();
        Seed(store);
        var sessions = new StreamSessionRepository(store);
        var id = sessions.Open(Account, "tsukuru-tv", T0, null, null);

        // Live: the window ends now, so every game after T0 counts.
        var live = sessions.Get(id)!;
        Assert.Equal((3, 1, 0), (live.Record.Wins, live.Record.Losses, live.Record.Ties));
        Assert.Equal(new[] { ("Control Priest", 3), ("Zoo Warlock", 1) }, live.Decks.Select(d => (d.Name, d.Games)).ToArray());

        // Closed before the last two games: they fall outside.
        sessions.Close(id, T0.AddMinutes(90), null);
        var closed = sessions.Get(id)!;
        Assert.Equal((2, 0, 0), (closed.Record.Wins, closed.Record.Losses, closed.Record.Ties));
        Assert.Single(closed.Decks);
        Assert.Equal(2, closed.Decks[0].Games);

        // A game before the start never counts.
        Assert.Equal(0, sessions.GamesIn(Account, T0.AddHours(-2), T0.AddHours(-1)).Record.Games);
        sessions.MarkSummaryPosted(id, T0.AddMinutes(91));
        Assert.Equal(T0.AddMinutes(91), sessions.Get(id)!.SummaryPostedAt);
    }

    private static void Seed(DustStore store)
    {
        Exec(store, "INSERT INTO account(account_key, battle_tag, region, first_seen_at, last_seen_at) VALUES ($k, 'Synthetic#0001', 'EU', $t, $t)", ("$k", Account), ("$t", DustStore.Iso(T0)));
        Exec(store, "INSERT INTO deck(id, account_key, origin, external_id, name, card_class, captured_at) VALUES (1, $k, 'hdt', 'd1', 'Control Priest', 'PRIEST', $t), (2, $k, 'hdt', 'd2', 'Zoo Warlock', 'WARLOCK', $t)", ("$k", Account), ("$t", DustStore.Iso(T0)));
        Game(store, "g0", T0.AddMinutes(-30), "win", 1);   // before the stream
        Game(store, "g1", T0.AddMinutes(10), "win", 1);
        Game(store, "g2", T0.AddMinutes(40), "win", 1);
        Game(store, "g3", T0.AddMinutes(100), "loss", 2);
        Game(store, "g4", T0.AddMinutes(130), "win", 1);
    }

    private static void Game(DustStore store, string key, DateTimeOffset startedAt, string result, long deckId) =>
        Exec(store, "INSERT INTO game(account_key, game_key, started_at, ended_at, result, deck_id) VALUES ($k, $g, $s, $e, $r, $d)",
            ("$k", Account), ("$g", key), ("$s", DustStore.Iso(startedAt)), ("$e", DustStore.Iso(startedAt.AddMinutes(8))), ("$r", result), ("$d", deckId));

    private static void Exec(DustStore store, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = store.Command(sql, parameters);
        cmd.ExecuteNonQuery();
    }
}
