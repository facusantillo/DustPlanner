using DustPlanner.Store;

namespace DustPlanner.LogTracker;

/// <summary>The ladder standing copied into a session at one of its ends: the label plus what the chat texts need.</summary>
public sealed record SessionRank(string Label, string Format, int LeagueId, int StarLevel, int LegendRank)
{
    public static SessionRank From(RankSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new SessionRank(RankRepository.Describe(sample), sample.Format, sample.LeagueId, sample.StarLevel, sample.LegendRank);
    }
}

/// <summary>A deck seen in a session's games: the deck table's name, and how many games were played with it.</summary>
public sealed record SessionDeck(long DeckId, string Name, string? CardClass, int Games);

/// <summary>One stream (Kick stage K7). Record and decks are computed from the games started inside its time range.</summary>
public sealed record StreamSession(
    long Id,
    string AccountKey,
    string Platform,
    string ChannelSlug,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Title,
    SessionRank? RankStart,
    SessionRank? RankEnd,
    DateTimeOffset? SummaryPostedAt,
    WinRate Record,
    IReadOnlyList<SessionDeck> Decks)
{
    public bool IsLive => EndedAt is null;
}

/// <summary>
/// Sessions are opened by the relay's <c>stream.status</c> with <c>is_live: true</c> and closed by the next
/// <c>is_live: false</c>. Nothing links games to sessions by id: a session's games are the ones that started between
/// its start and its end (or now, while live), which also makes games imported later count.
/// </summary>
public sealed class StreamSessionRepository
{
    private readonly DustStore _store;

    public StreamSessionRepository(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Opens a session, or returns the one already open for the same start (the relay repeats the live state in every
    /// hello). A different start while another session is still open closes that one first, at the new start.
    /// </summary>
    public long Open(string accountKey, string channelSlug, DateTimeOffset startedAt, string? title, SessionRank? rankStart, string platform = "kick")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        var existing = FindByStart(accountKey, platform, startedAt);
        if (existing is not null)
        {
            if (title is not null && existing.Title != title)
            {
                using var rename = _store.Command("UPDATE stream_session SET title = $t WHERE id = $id", ("$t", title), ("$id", existing.Id));
                rename.ExecuteNonQuery();
            }

            return existing.Id;
        }

        var open = LatestOpen(accountKey, platform);
        if (open is not null)
        {
            Close(open.Id, startedAt, null);
        }

        using var cmd = _store.Command(
            """
            INSERT INTO stream_session(account_key, platform, channel_slug, started_at, title, rank_start, rank_start_format, rank_start_league, rank_start_star_level, rank_start_legend)
            VALUES ($k, $p, $s, $at, $t, $rl, $rf, $rlg, $rsl, $rlr);
            SELECT last_insert_rowid();
            """,
            ("$k", accountKey), ("$p", platform), ("$s", channelSlug), ("$at", DustStore.Iso(startedAt)), ("$t", title),
            ("$rl", rankStart?.Label), ("$rf", rankStart?.Format), ("$rlg", rankStart?.LeagueId), ("$rsl", rankStart?.StarLevel), ("$rlr", rankStart?.LegendRank));
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Closes a session; no-op when it is already closed (the relay may repeat the end).</summary>
    public bool Close(long id, DateTimeOffset endedAt, SessionRank? rankEnd)
    {
        using var cmd = _store.Command(
            """
            UPDATE stream_session SET ended_at = $e, rank_end = $rl, rank_end_format = $rf, rank_end_league = $rlg, rank_end_star_level = $rsl, rank_end_legend = $rlr
            WHERE id = $id AND ended_at IS NULL
            """,
            ("$e", DustStore.Iso(endedAt)), ("$rl", rankEnd?.Label), ("$rf", rankEnd?.Format), ("$rlg", rankEnd?.LeagueId), ("$rsl", rankEnd?.StarLevel), ("$rlr", rankEnd?.LegendRank), ("$id", id));
        return cmd.ExecuteNonQuery() > 0;
    }

    public void MarkSummaryPosted(long id, DateTimeOffset at)
    {
        using var cmd = _store.Command("UPDATE stream_session SET summary_posted_at = $at WHERE id = $id", ("$at", DustStore.Iso(at)), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The open session of an account, or of any account when <paramref name="accountKey"/> is null.</summary>
    public StreamSession? LatestOpen(string? accountKey, string platform = "kick") =>
        Query("WHERE ($k IS NULL OR account_key = $k) AND platform = $p AND ended_at IS NULL ORDER BY started_at DESC LIMIT 1", ("$k", accountKey), ("$p", platform)).FirstOrDefault();

    public StreamSession? Get(long id) => Query("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<StreamSession> List(string? accountKey, int limit = 20) =>
        Query("WHERE ($k IS NULL OR account_key = $k) ORDER BY started_at DESC LIMIT $n", ("$k", accountKey), ("$n", limit));

    private StreamSession? FindByStart(string accountKey, string platform, DateTimeOffset startedAt) =>
        Query("WHERE account_key = $k AND platform = $p AND started_at = $s", ("$k", accountKey), ("$p", platform), ("$s", DustStore.Iso(startedAt))).FirstOrDefault();

    private List<StreamSession> Query(string whereOrderLimit, params (string Name, object? Value)[] parameters)
    {
        var rows = new List<(long Id, string Account, string Platform, string Slug, DateTimeOffset Started, DateTimeOffset? Ended, string? Title, SessionRank? Start, SessionRank? End, DateTimeOffset? Posted)>();
        using (var cmd = _store.Command(
            """
            SELECT id, account_key, platform, channel_slug, started_at, ended_at, title,
                   rank_start, rank_start_format, rank_start_league, rank_start_star_level, rank_start_legend,
                   rank_end, rank_end_format, rank_end_league, rank_end_star_level, rank_end_legend, summary_posted_at
            FROM stream_session
            """ + " " + whereOrderLimit, parameters))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DustStore.ParseIso(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : DustStore.ParseIso(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6),
                    ReadRank(reader, 7), ReadRank(reader, 12), reader.IsDBNull(17) ? null : DustStore.ParseIso(reader.GetString(17))));
            }
        }

        var result = new List<StreamSession>(rows.Count);
        foreach (var row in rows)
        {
            var (record, decks) = GamesIn(row.Account, row.Started, row.Ended);
            result.Add(new StreamSession(row.Id, row.Account, row.Platform, row.Slug, row.Started, row.Ended, row.Title, row.Start, row.End, row.Posted, record, decks));
        }

        return result;
    }

    private static SessionRank? ReadRank(Microsoft.Data.Sqlite.SqliteDataReader reader, int first) =>
        reader.IsDBNull(first)
            ? null
            : new SessionRank(reader.GetString(first), reader.IsDBNull(first + 1) ? string.Empty : reader.GetString(first + 1), reader.IsDBNull(first + 2) ? 0 : reader.GetInt32(first + 2), reader.IsDBNull(first + 3) ? 0 : reader.GetInt32(first + 3), reader.IsDBNull(first + 4) ? 0 : reader.GetInt32(first + 4));

    /// <summary>Record and decks of the games that started inside the window; an open window ends now.</summary>
    public (WinRate Record, IReadOnlyList<SessionDeck> Decks) GamesIn(string accountKey, DateTimeOffset from, DateTimeOffset? to)
    {
        var wins = 0;
        var losses = 0;
        var ties = 0;
        var unknown = 0;
        var decks = new Dictionary<long, (string Name, string? CardClass, int Games)>();
        using var cmd = _store.Command(
            """
            SELECT g.result, g.deck_id, d.name, d.card_class
            FROM game g LEFT JOIN deck d ON d.id = g.deck_id
            WHERE g.account_key = $k AND g.started_at >= $from AND ($to IS NULL OR g.started_at < $to)
            ORDER BY g.started_at
            """,
            ("$k", accountKey), ("$from", DustStore.Iso(from)), ("$to", to is null ? null : DustStore.Iso(to.Value)));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            switch (reader.GetString(0))
            {
                case "win":
                    wins++;
                    break;
                case "loss":
                    losses++;
                    break;
                case "tie":
                    ties++;
                    break;
                default:
                    unknown++;
                    break;
            }

            if (!reader.IsDBNull(1))
            {
                var deckId = reader.GetInt64(1);
                if (!decks.TryGetValue(deckId, out var current))
                {
                    current = (reader.IsDBNull(2) ? "?" : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), 0);
                }

                decks[deckId] = (current.Name, current.CardClass, current.Games + 1);
            }
        }

        var list = decks.OrderByDescending(p => p.Value.Games).ThenBy(p => p.Value.Name, StringComparer.Ordinal).Select(p => new SessionDeck(p.Key, p.Value.Name, p.Value.CardClass, p.Value.Games)).ToList();
        return (new WinRate(wins, losses, ties, unknown), list);
    }
}
