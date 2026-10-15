using DustPlanner.Contracts;

namespace DustPlanner.Store;

public sealed record RankSample(string AccountKey, DateTimeOffset TakenAt, string Format, int LeagueId, int StarLevel, int Stars, int LegendRank, int SeasonId, int SeasonGames, int SeasonWins);

/// <summary>Ranked standings per format, as exported by the HDT plugin (never parsed from Power.log — decision Q8).</summary>
public sealed class RankRepository
{
    private readonly DustStore _store;

    public RankRepository(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public int Import(RankExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var count = 0;
        using var tx = _store.BeginTransaction();
        foreach (var (format, info) in export.Formats)
        {
            using var cmd = _store.Command(
                """
                INSERT OR IGNORE INTO rank_sample(account_key, taken_at, format, league_id, star_level, stars, legend_rank, season_id, season_games, season_wins)
                VALUES ($k, $t, $f, $l, $sl, $s, $lr, $sid, $sg, $sw)
                """,
                ("$k", export.Meta.Account.AccountKey), ("$t", DustStore.Iso(export.Meta.ExportedAt)), ("$f", format), ("$l", info.LeagueId), ("$sl", info.StarLevel),
                ("$s", info.Stars), ("$lr", info.LegendRank), ("$sid", info.SeasonId), ("$sg", info.SeasonGames), ("$sw", info.SeasonWins));
            cmd.Transaction = tx;
            count += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return count;
    }

    public RankSample? Latest(string accountKey, string format)
    {
        using var cmd = _store.Command(
            "SELECT account_key, taken_at, format, league_id, star_level, stars, legend_rank, season_id, season_games, season_wins FROM rank_sample WHERE account_key = $k AND format = $f ORDER BY taken_at DESC LIMIT 1",
            ("$k", accountKey), ("$f", format));
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new RankSample(reader.GetString(0), DustStore.ParseIso(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9))
            : null;
    }

    /// <summary>Human label for a ladder standing: Bronze..Diamond with star level, or Legend rank.</summary>
    public static string Describe(RankSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.LegendRank > 0)
        {
            return $"Legend #{sample.LegendRank}";
        }

        var league = sample.LeagueId switch
        {
            1 => "Bronze",
            2 => "Silver",
            3 => "Gold",
            4 => "Platinum",
            5 => "Diamond",
            _ => $"League {sample.LeagueId}",
        };
        var rank = sample.StarLevel > 0 ? 11 - sample.StarLevel : 0;
        return rank > 0 ? $"{league} {rank} ({sample.Stars} stars)" : league;
    }
}
