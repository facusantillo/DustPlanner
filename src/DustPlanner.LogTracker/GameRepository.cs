using DustPlanner.Core.Cards;
using DustPlanner.Store;

namespace DustPlanner.LogTracker;

public sealed record StoredGame(long Id, string AccountKey, string GameKey, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? GameType, string? Format,
    string? PlayerClass, string? OpponentClass, string? PlayerHeroCardId, string? OpponentHeroCardId, string? OpponentName, string Result, bool? WentFirst, int? Turns);

/// <summary>Stores parsed games in the <c>game</c> table (stage-5 schema) and answers win-rate questions.</summary>
public sealed class GameRepository
{
    private readonly DustStore _store;

    public GameRepository(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Inserts the game unless its key is already stored. Returns true when inserted.</summary>
    public bool Insert(GameRecord game, CardCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(game);
        var friendly = game.Friendly;
        var opponent = game.Opponent;
        if (friendly is null)
        {
            return false;
        }

        using var cmd = _store.Command(
            """
            INSERT OR IGNORE INTO game(account_key, game_key, started_at, ended_at, game_type, format, player_class, opponent_class,
                                       player_hero_card_id, opponent_hero_card_id, opponent_name, result, went_first, turns, source)
            VALUES ($k, $key, $start, $end, $type, $fmt, $pc, $oc, $ph, $oh, $on, $res, $first, $turns, 'powerlog')
            """,
            ("$k", friendly.AccountKey), ("$key", game.Key), ("$start", DustStore.Iso(game.StartedAt)), ("$end", game.EndedAt is { } e ? DustStore.Iso(e) : null),
            ("$type", game.GameType), ("$fmt", game.FormatType), ("$pc", HeroClasses.ClassOf(friendly.HeroCardId, catalog)), ("$oc", HeroClasses.ClassOf(opponent?.HeroCardId, catalog)),
            ("$ph", friendly.HeroCardId), ("$oh", opponent?.HeroCardId), ("$on", opponent?.Name), ("$res", game.Result), ("$first", friendly.IsFirst ? 1 : 0), ("$turns", game.Turns));
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<StoredGame> List(string? accountKey, int limit = 50)
    {
        using var cmd = _store.Command(
            """
            SELECT id, account_key, game_key, started_at, ended_at, game_type, format, player_class, opponent_class, player_hero_card_id, opponent_hero_card_id, opponent_name, result, went_first, turns
            FROM game WHERE ($k IS NULL OR account_key = $k) ORDER BY started_at DESC, id DESC LIMIT $n
            """,
            ("$k", accountKey), ("$n", limit));
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredGame>();
        while (reader.Read())
        {
            list.Add(new StoredGame(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DustStore.ParseIso(reader.GetString(3)),
                reader.IsDBNull(4) ? null : DustStore.ParseIso(reader.GetString(4)), Str(reader, 5), Str(reader, 6), Str(reader, 7), Str(reader, 8), Str(reader, 9), Str(reader, 10), Str(reader, 11),
                reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt32(13) != 0, reader.IsDBNull(14) ? null : reader.GetInt32(14)));
        }

        return list;
    }

    /// <summary>Account of the most recent stored game, if any.</summary>
    public string? LatestAccount()
    {
        using var cmd = _store.Command("SELECT account_key FROM game ORDER BY started_at DESC, id DESC LIMIT 1");
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<string> Accounts()
    {
        using var cmd = _store.Command("SELECT DISTINCT account_key FROM game ORDER BY account_key");
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Wins/losses/ties since <paramref name="since"/> (UTC), optionally filtered by game type (e.g. <c>GT_RANKED</c>) and format.</summary>
    public WinRate Stats(string? accountKey, DateTimeOffset? since, string? gameType = null, string? format = null, string? playerClass = null)
    {
        using var cmd = _store.Command(
            """
            SELECT result, COUNT(*) FROM game
            WHERE ($k IS NULL OR account_key = $k) AND ($since IS NULL OR started_at >= $since)
              AND ($type IS NULL OR game_type = $type) AND ($fmt IS NULL OR format = $fmt) AND ($cls IS NULL OR player_class = $cls)
            GROUP BY result
            """,
            ("$k", accountKey), ("$since", since is { } s ? DustStore.Iso(s) : null), ("$type", gameType), ("$fmt", format), ("$cls", playerClass));
        using var reader = cmd.ExecuteReader();
        int wins = 0, losses = 0, ties = 0, unknown = 0;
        while (reader.Read())
        {
            var count = reader.GetInt32(1);
            switch (reader.GetString(0))
            {
                case "win": wins = count; break;
                case "loss": losses = count; break;
                case "tie": ties = count; break;
                default: unknown = count; break;
            }
        }

        return new WinRate(wins, losses, ties, unknown);
    }

    /// <summary>Win rate per player class since <paramref name="since"/>.</summary>
    public IReadOnlyList<(string PlayerClass, WinRate Rate)> StatsByClass(string? accountKey, DateTimeOffset? since, string? gameType = null, string? format = null)
    {
        using var cmd = _store.Command(
            """
            SELECT COALESCE(player_class, '?'), result, COUNT(*) FROM game
            WHERE ($k IS NULL OR account_key = $k) AND ($since IS NULL OR started_at >= $since) AND ($type IS NULL OR game_type = $type) AND ($fmt IS NULL OR format = $fmt)
            GROUP BY player_class, result
            """,
            ("$k", accountKey), ("$since", since is { } s ? DustStore.Iso(s) : null), ("$type", gameType), ("$fmt", format));
        using var reader = cmd.ExecuteReader();
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var cls = reader.GetString(0);
            var arr = counts.TryGetValue(cls, out var a) ? a : counts[cls] = new int[4];
            var count = reader.GetInt32(2);
            switch (reader.GetString(1))
            {
                case "win": arr[0] += count; break;
                case "loss": arr[1] += count; break;
                case "tie": arr[2] += count; break;
                default: arr[3] += count; break;
            }
        }

        return counts.Select(p => (p.Key, new WinRate(p.Value[0], p.Value[1], p.Value[2], p.Value[3]))).OrderByDescending(x => x.Item2.Games).ToList();
    }

    private static string? Str(Microsoft.Data.Sqlite.SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
}

public sealed record WinRate(int Wins, int Losses, int Ties, int Unknown)
{
    public int Games => Wins + Losses + Ties + Unknown;

    /// <summary>Wins over decided games (ties and unknown results excluded); null when nothing was decided.</summary>
    public double? Percentage => Wins + Losses == 0 ? null : (double)Wins / (Wins + Losses);
}

/// <summary>Maps hero card ids to classes: catalog first (skins are collectible HERO cards), then the classic numbering.</summary>
public static class HeroClasses
{
    private static readonly Dictionary<string, string> ByPrefix = new(StringComparer.Ordinal)
    {
        ["HERO_01"] = "WARRIOR",
        ["HERO_02"] = "SHAMAN",
        ["HERO_03"] = "ROGUE",
        ["HERO_04"] = "PALADIN",
        ["HERO_05"] = "HUNTER",
        ["HERO_06"] = "DRUID",
        ["HERO_07"] = "WARLOCK",
        ["HERO_08"] = "MAGE",
        ["HERO_09"] = "PRIEST",
        ["HERO_10"] = "DEMONHUNTER",
        ["HERO_11"] = "DEATHKNIGHT",
    };

    public static string? ClassOf(string? heroCardId, CardCatalog? catalog)
    {
        if (string.IsNullOrEmpty(heroCardId))
        {
            return null;
        }

        var card = catalog?.GetByCardId(heroCardId);
        if (card is not null && !card.IsNeutral)
        {
            return card.CardClass;
        }

        return heroCardId.Length >= 7 && ByPrefix.TryGetValue(heroCardId[..7], out var cls) ? cls : null;
    }
}
