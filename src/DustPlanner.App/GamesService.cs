using System.Globalization;
using DustPlanner.App.Reports;
using DustPlanner.Core.Cards;
using DustPlanner.LogTracker;
using DustPlanner.Store;

namespace DustPlanner.App;

public sealed record GamesImportJson(string LogsDirectory, int FilesRead, int GamesParsed, int GamesInserted, int Incomplete, IReadOnlyList<string> Warnings);

public sealed record ClassStatsJson(string PlayerClass, StatsJson Stats);

/// <summary>Win rate overall and per class plus the most recent games (the filters apply to the stats only).</summary>
public sealed record GamesOverviewJson(string? AccountKey, IReadOnlyList<string> KnownAccounts, DateTimeOffset? Since, string? GameType, string? Format,
    StatsJson Overall, IReadOnlyList<ClassStatsJson> ByClass, IReadOnlyList<StoredGame> RecentGames);

public sealed partial class AppServices
{
    /// <summary>Parses every Power.log in the logs folder (found through HDT's config when not given) into the database. Safe to re-run.</summary>
    public GamesImportJson ImportGames(string? logsDirectory, CardCatalog? catalog)
    {
        var logs = logsDirectory is not null
            ? (Directory.Exists(logsDirectory) ? Path.GetFullPath(logsDirectory) : throw new AppException($"Logs folder not found: {logsDirectory}"))
            : LogLocator.FindLogsDirectory() ?? throw new AppException("Hearthstone's Logs folder was not found. Pass the path to Hearthstone\\Logs.");

        Directory.CreateDirectory(Paths.Root);
        using var store = DustStore.Open(Paths.Database);
        var summary = new LogImporter(new GameRepository(store), catalog).ImportDirectory(logs);
        return new GamesImportJson(logs, summary.FilesRead, summary.GamesParsed, summary.GamesInserted, summary.Incomplete, summary.Warnings);
    }

    /// <summary>Stats for the account (default: the account of the most recent game) over the period and filters, plus the latest games.</summary>
    public GamesOverviewJson GamesOverview(string? account, DateTimeOffset? since, string? gameType, string? format, int recent)
    {
        if (!File.Exists(Paths.Database))
        {
            return new GamesOverviewJson(null, Array.Empty<string>(), since, gameType, format, StatsJson.From(new WinRate(0, 0, 0, 0)), Array.Empty<ClassStatsJson>(), Array.Empty<StoredGame>());
        }

        using var store = DustStore.Open(Paths.Database, readOnly: true);
        var games = new GameRepository(store);
        var accountKey = account ?? games.LatestAccount();
        var overall = games.Stats(accountKey, since, gameType, format);
        var byClass = games.StatsByClass(accountKey, since, gameType, format).Select(c => new ClassStatsJson(c.PlayerClass, StatsJson.From(c.Rate))).ToList();
        var list = accountKey is null ? Array.Empty<StoredGame>() : games.List(accountKey, Math.Clamp(recent, 0, 200));
        return new GamesOverviewJson(accountKey, games.Accounts(), since, gameType, format, StatsJson.From(overall), byClass, list);
    }

    /// <summary><c>today</c>, <c>YYYY-MM-DD</c> or a number of days, as an instant (local midnight in UTC).</summary>
    public static DateTimeOffset? ParseSince(string? since, int? days)
    {
        if (days is { } d)
        {
            return DateTimeOffset.Now.Date.AddDays(-(Math.Max(d, 1) - 1)).ToUniversalTime();
        }

        if (string.IsNullOrWhiteSpace(since))
        {
            return null;
        }

        if (since.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return new DateTimeOffset(DateTime.Today).ToUniversalTime();
        }

        if (DateOnly.TryParseExact(since.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)).ToUniversalTime();
        }

        throw new AppException($"Expected YYYY-MM-DD or 'today', got '{since}'.");
    }
}
