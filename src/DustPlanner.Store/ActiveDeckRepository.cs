using DustPlanner.Contracts;
using Microsoft.Data.Sqlite;

namespace DustPlanner.Store;

public sealed record ActiveDeckSample(long Id, string AccountKey, DateTimeOffset TakenAt, string? HdtDeckId, string Name, string Deckstring, string? CardClass, string? Format, string Reason);

/// <summary>
/// Samples of HDT's active deck (<c>active-deck.json</c>) and the link between a game and the deck it was played with.
/// A game is linked to the newest sample taken between ten minutes before and one minute after its start, of the same
/// class; the deck row (<c>origin = hdt</c>, <c>external_id = hdt_deck_id</c>) is created when the decks export has not
/// brought it yet. Nothing here reads the game: the sample comes from HDT's own deck list.
/// </summary>
public sealed class ActiveDeckRepository
{
    /// <summary>Samples older than this are pruned on every import.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>How long before a game's start a sample still counts (the deck was picked, then the queue took a while).</summary>
    public static readonly TimeSpan BeforeStart = TimeSpan.FromMinutes(10);

    /// <summary>How long after a game's start a sample still counts (the export runs on OnGameStart, next to the log line).</summary>
    public static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(60);

    private readonly DustStore _store;

    public ActiveDeckRepository(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Stores the export's sample (nothing when <c>deck</c> is null), prunes old samples and links the games started
    /// around it that have no deck yet. Returns the number of samples inserted (0 or 1).
    /// </summary>
    public int Import(ActiveDeckExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var account = export.Meta.Account;
        var accountKey = account.AccountKey;
        new SnapshotRepository(_store).UpsertAccount(accountKey, account.BattleTag, account.Region, export.Meta.ExportedAt);
        Prune(accountKey, export.Meta.ExportedAt - Retention);

        var deck = export.Deck;
        if (deck is null || string.IsNullOrEmpty(deck.Deckstring))
        {
            return 0;
        }

        var inserted = _store.Execute(
            """
            INSERT OR IGNORE INTO active_deck(account_key, taken_at, hdt_deck_id, name, deckstring, card_class, format, reason)
            VALUES ($k, $t, $id, $n, $ds, $c, $f, $r)
            """,
            ("$k", accountKey), ("$t", DustStore.Iso(export.Meta.ExportedAt)), ("$id", deck.HdtDeckId), ("$n", deck.Name), ("$ds", deck.Deckstring),
            ("$c", deck.CardClass?.ToUpperInvariant()), ("$f", deck.Format), ("$r", export.Meta.Trigger));

        LinkUnlinkedGames(accountKey, export.Meta.ExportedAt - AfterStart, export.Meta.ExportedAt + BeforeStart);
        return inserted;
    }

    public ActiveDeckSample? Latest(string accountKey)
    {
        using var cmd = _store.Command(
            "SELECT id, account_key, taken_at, hdt_deck_id, name, deckstring, card_class, format, reason FROM active_deck WHERE account_key = $k ORDER BY taken_at DESC, id DESC LIMIT 1",
            ("$k", accountKey));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<ActiveDeckSample> List(string accountKey, int limit = 50)
    {
        using var cmd = _store.Command(
            "SELECT id, account_key, taken_at, hdt_deck_id, name, deckstring, card_class, format, reason FROM active_deck WHERE account_key = $k ORDER BY taken_at DESC, id DESC LIMIT $n",
            ("$k", accountKey), ("$n", limit));
        using var reader = cmd.ExecuteReader();
        var list = new List<ActiveDeckSample>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }

        return list;
    }

    /// <summary>The sample a game started at <paramref name="startedAt"/> with class <paramref name="playerClass"/> was most likely played with.</summary>
    public ActiveDeckSample? FindFor(string accountKey, DateTimeOffset startedAt, string? playerClass)
    {
        if (string.IsNullOrEmpty(playerClass))
        {
            // Without a class the match would rest on timing alone; the conservative answer is no link.
            return null;
        }

        using var cmd = _store.Command(
            """
            SELECT id, account_key, taken_at, hdt_deck_id, name, deckstring, card_class, format, reason FROM active_deck
            WHERE account_key = $k AND taken_at >= $from AND taken_at <= $to AND card_class = $c
            ORDER BY taken_at DESC, id DESC LIMIT 1
            """,
            ("$k", accountKey), ("$from", DustStore.Iso(startedAt - BeforeStart)), ("$to", DustStore.Iso(startedAt + AfterStart)), ("$c", playerClass.ToUpperInvariant()));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Sets <c>game.deck_id</c> for one game when a matching sample exists. Returns the deck id, or null when no sample matched.</summary>
    public long? LinkGame(string accountKey, string gameKey, DateTimeOffset startedAt, string? playerClass)
    {
        var sample = FindFor(accountKey, startedAt, playerClass);
        if (sample is null)
        {
            return null;
        }

        var deckId = EnsureDeck(sample);
        _store.Execute("UPDATE game SET deck_id = $d WHERE game_key = $g AND deck_id IS NULL", ("$d", deckId), ("$g", gameKey));
        return deckId;
    }

    /// <summary>Links every game of the account without a deck that started in the given window. Returns how many were linked.</summary>
    public int LinkUnlinkedGames(string accountKey, DateTimeOffset from, DateTimeOffset to)
    {
        var candidates = new List<(string Key, DateTimeOffset StartedAt, string? Class)>();
        using (var cmd = _store.Command(
                   "SELECT game_key, started_at, player_class FROM game WHERE account_key = $k AND deck_id IS NULL AND started_at >= $from AND started_at <= $to",
                   ("$k", accountKey), ("$from", DustStore.Iso(from)), ("$to", DustStore.Iso(to))))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add((reader.GetString(0), DustStore.ParseIso(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var linked = 0;
        foreach (var (key, startedAt, cls) in candidates)
        {
            if (LinkGame(accountKey, key, startedAt, cls) is not null)
            {
                linked++;
            }
        }

        return linked;
    }

    /// <summary>The deck row for a sample: the one the decks export created (origin hdt, same GUID) or a minimal new one.</summary>
    public long EnsureDeck(ActiveDeckSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var externalId = sample.HdtDeckId ?? "deckstring:" + sample.Deckstring;
        var existing = _store.Scalar("SELECT id FROM deck WHERE account_key = $k AND origin = 'hdt' AND external_id = $e", ("$k", sample.AccountKey), ("$e", externalId));
        if (existing is long id)
        {
            return id;
        }

        _store.Execute(
            """
            INSERT INTO deck(account_key, origin, external_id, name, card_class, format_type, deckstring, archived, captured_at)
            VALUES ($k, 'hdt', $e, $n, $c, $f, $ds, 0, $t)
            """,
            ("$k", sample.AccountKey), ("$e", externalId), ("$n", sample.Name), ("$c", sample.CardClass), ("$f", FormatTypeOf(sample.Format)), ("$ds", sample.Deckstring), ("$t", DustStore.Iso(sample.TakenAt)));
        return (long)_store.Scalar("SELECT last_insert_rowid()")!;
    }

    /// <summary>HearthMirror's numbering, the one <c>deck.format_type</c> uses: 1 Wild, 2 Standard, 3 Classic, 4 Twist.</summary>
    public static int? FormatTypeOf(string? format) => format switch
    {
        DeckFormats.Wild => 1,
        DeckFormats.Standard => 2,
        DeckFormats.Classic => 3,
        DeckFormats.Twist => 4,
        _ => null,
    };

    private void Prune(string accountKey, DateTimeOffset olderThan) =>
        _store.Execute("DELETE FROM active_deck WHERE account_key = $k AND taken_at < $t", ("$k", accountKey), ("$t", DustStore.Iso(olderThan)));

    private static ActiveDeckSample Read(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), DustStore.ParseIso(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8));
}
