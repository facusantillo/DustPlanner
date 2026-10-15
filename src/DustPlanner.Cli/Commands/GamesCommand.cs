using System.CommandLine;
using DustPlanner.App;
using DustPlanner.App.Reports;
using System.Globalization;
using DustPlanner.Cli.Rendering;
using DustPlanner.LogTracker;
using DustPlanner.Store;

namespace DustPlanner.Cli.Commands;

/// <summary>
/// <c>games</c>: match history read from the Power.log files Hearthstone writes on its own. Nothing here talks to the
/// game client; the only file this group can write is Blizzard's <c>log.config</c>, and only with <c>--apply</c>.
/// </summary>
internal static class GamesCommand
{
    public static Command Build(Func<ParseResult, CliContext> context)
    {
        var command = new Command("games", "Match history parsed from Hearthstone's own Power.log files (win rate, classes, opponents).");
        command.Subcommands.Add(BuildImport(context));
        command.Subcommands.Add(BuildList(context));
        command.Subcommands.Add(BuildStats(context));
        command.Subcommands.Add(BuildLogConfig(context));
        return command;
    }

    private static Command BuildImport(Func<ParseResult, CliContext> context)
    {
        var logsOpt = new Option<string?>("--logs") { Description = "Hearthstone 'Logs' folder (default: found through HDT's config or the usual install paths)." };
        var command = new Command("import", "Parse every Power.log in the logs folder and store the games found (safe to re-run).") { logsOpt };

        command.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            var logs = ResolveLogs(result.GetValue(logsOpt));
            var catalog = (await ctx.LoadCatalogAsync(forceRefresh: false, "enUS", ct).ConfigureAwait(false)).Catalog;
            using var store = DustStore.Open(ctx.Paths.Database);
            var summary = new LogImporter(new GameRepository(store), catalog).ImportDirectory(logs);

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    logs_directory = logs,
                    files_read = summary.FilesRead,
                    games_parsed = summary.GamesParsed,
                    games_inserted = summary.GamesInserted,
                    incomplete = summary.Incomplete,
                    warnings = summary.Warnings,
                }).ConfigureAwait(false);
                return 0;
            }

            await ctx.Out.WriteLineAsync($"{logs}: {summary.FilesRead} Power.log file(s), {summary.GamesParsed} game(s) parsed, {summary.GamesInserted} new, {summary.Incomplete} without a result").ConfigureAwait(false);
            foreach (var warning in summary.Warnings)
            {
                await ctx.Out.WriteLineAsync("  warning: " + warning).ConfigureAwait(false);
            }

            if (summary.FilesRead == 0)
            {
                await ctx.Out.WriteLineAsync("  No Power.log found. Run 'dustplanner games log-config' to check that Hearthstone writes it, then play a game.").ConfigureAwait(false);
            }

            return 0;
        }));

        return command;
    }

    private static Command BuildList(Func<ParseResult, CliContext> context)
    {
        var accountOpt = new Option<string?>("--account", "-a") { Description = "Account key (default: the account of the most recent game)." };
        var limitOpt = new Option<int>("--limit") { Description = "Maximum games to show.", DefaultValueFactory = _ => 30 };
        var command = new Command("list", "Most recent games, newest first.") { accountOpt, limitOpt };

        command.SetAction(CliAction.Guarded(async (result, _) =>
        {
            var ctx = context(result);
            using var store = OpenReadOnly(ctx);
            var games = new GameRepository(store);
            var accountKey = ResolveAccount(games, result.GetValue(accountOpt));
            var list = games.List(accountKey, result.GetValue(limitOpt));

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new { account_key = accountKey, games = list }).ConfigureAwait(false);
                return 0;
            }

            await ctx.Out.WriteLineAsync($"Account {accountKey} · {list.Count} game(s)").ConfigureAwait(false);
            if (list.Count == 0)
            {
                return 0;
            }

            var table = new TextTable().AddColumn("started").AddColumn("mode").AddColumn("format").AddColumn("class").AddColumn("vs").AddColumn("opponent").AddColumn("result").AddColumn("first").AddColumn("turns", true);
            foreach (var g in list)
            {
                table.AddRow(g.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Mode(g.GameType), Format(g.Format), Title(g.PlayerClass), Title(g.OpponentClass),
                    g.OpponentName ?? "?", g.Result, g.WentFirst is { } f ? (f ? "yes" : "no") : "?", g.Turns is { } t ? t.ToString(CultureInfo.InvariantCulture) : "?");
            }

            await ctx.Out.WriteAsync(table.Render()).ConfigureAwait(false);
            return 0;
        }));

        return command;
    }

    private static Command BuildStats(Func<ParseResult, CliContext> context)
    {
        var accountOpt = new Option<string?>("--account", "-a") { Description = "Account key (default: the account of the most recent game)." };
        var sinceOpt = new Option<string?>("--since") { Description = "Only games started on/after this local date (YYYY-MM-DD) or 'today'." };
        var daysOpt = new Option<int?>("--days") { Description = "Only the last N days." };
        var typeOpt = new Option<string?>("--type") { Description = "Game type filter, e.g. GT_RANKED, GT_CASUAL, GT_ARENA." };
        var formatOpt = new Option<string?>("--format") { Description = "Format filter: FT_STANDARD or FT_WILD." };
        var command = new Command("stats", "Win rate overall and per class.") { accountOpt, sinceOpt, daysOpt, typeOpt, formatOpt };

        command.SetAction(CliAction.Guarded(async (result, _) =>
        {
            var ctx = context(result);
            using var store = OpenReadOnly(ctx);
            var games = new GameRepository(store);
            var accountKey = ResolveAccount(games, result.GetValue(accountOpt));
            var since = ParseSince(result.GetValue(sinceOpt), result.GetValue(daysOpt));
            var gameType = result.GetValue(typeOpt);
            var format = result.GetValue(formatOpt);

            var overall = games.Stats(accountKey, since, gameType, format);
            var byClass = games.StatsByClass(accountKey, since, gameType);

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    account_key = accountKey,
                    since,
                    game_type = gameType,
                    format,
                    overall = StatsJson.From(overall),
                    by_class = byClass.Select(c => new { player_class = c.PlayerClass, stats = StatsJson.From(c.Rate) }),
                }).ConfigureAwait(false);
                return 0;
            }

            var scope = (since is { } s ? $"since {s.ToLocalTime():yyyy-MM-dd}" : "all time") + (gameType is not null ? $" · {Mode(gameType)}" : string.Empty) + (format is not null ? $" · {Format(format)}" : string.Empty);
            await ctx.Out.WriteLineAsync($"Account {accountKey} · {scope}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"  {overall.Games} game(s): {overall.Wins} won, {overall.Losses} lost, {overall.Ties} tied, {overall.Unknown} unknown · win rate {Percent(overall)}").ConfigureAwait(false);
            if (byClass.Count > 0)
            {
                var table = new TextTable().AddColumn("class").AddColumn("games", true).AddColumn("won", true).AddColumn("lost", true).AddColumn("win rate", true);
                foreach (var (cls, rate) in byClass)
                {
                    table.AddRow(Title(cls), rate.Games, rate.Wins, rate.Losses, Percent(rate));
                }

                await ctx.Out.WriteAsync(table.Render()).ConfigureAwait(false);
            }

            return 0;
        }));

        return command;
    }

    private static Command BuildLogConfig(Func<ParseResult, CliContext> context)
    {
        var pathOpt = new Option<string?>("--path") { Description = "log.config path (default: %LocalAppData%\\Blizzard\\Hearthstone\\log.config)." };
        var applyOpt = new Option<bool>("--apply") { Description = "Write the [Power] section when it is missing or incomplete. Without it the command only reports." };
        var command = new Command("log-config", "Check (or, with --apply, complete) Blizzard's log.config so Hearthstone writes Power.log. Other sections are left untouched.") { pathOpt, applyOpt };

        command.SetAction(CliAction.Guarded(async (result, _) =>
        {
            var ctx = context(result);
            var path = result.GetValue(pathOpt) ?? LogConfig.DefaultPath;
            var exists = File.Exists(path);
            var configured = exists && LogConfig.IsConfigured(File.ReadAllText(path));
            var applied = false;
            if (!configured && result.GetValue(applyOpt))
            {
                applied = LogConfig.EnsureFile(path);
                configured = true;
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new { path, exists, configured, applied, required = LogConfig.RequiredPowerSettings }).ConfigureAwait(false);
                return configured ? 0 : 1;
            }

            await ctx.Out.WriteLineAsync($"{path}: {(exists ? "exists" : "missing")}, [Power] section {(configured ? "complete" : "missing or incomplete")}" + (applied ? " (written)" : string.Empty)).ConfigureAwait(false);
            if (!configured)
            {
                await ctx.Out.WriteLineAsync("  Run again with --apply to add it (Hearthstone Deck Tracker normally does this for you). Restart Hearthstone afterwards.").ConfigureAwait(false);
            }

            return configured ? 0 : 1;
        }));

        return command;
    }

    private static string ResolveLogs(string? explicitLogs)
    {
        if (explicitLogs is not null)
        {
            return Directory.Exists(explicitLogs) ? Path.GetFullPath(explicitLogs) : throw new AppException($"Logs folder not found: {explicitLogs}");
        }

        return LogLocator.FindLogsDirectory()
            ?? throw new AppException("Hearthstone's Logs folder was not found. Pass --logs <path-to-Hearthstone\\Logs>.");
    }

    private static DustStore OpenReadOnly(CliContext ctx)
    {
        if (!File.Exists(ctx.Paths.Database))
        {
            throw new AppException($"No database yet at {ctx.Paths.Database}. Run 'dustplanner games import' first.");
        }

        return DustStore.Open(ctx.Paths.Database, readOnly: true);
    }

    private static string ResolveAccount(GameRepository games, string? requested) =>
        requested ?? games.LatestAccount() ?? throw new AppException("No games stored yet. Run 'dustplanner games import' first.");

    internal static DateTimeOffset? ParseSince(string? since, int? days)
    {
        if (days is { } d)
        {
            return DateTimeOffset.Now.Date.AddDays(-(d - 1)).ToUniversalTime();
        }

        if (since is null)
        {
            return null;
        }

        if (since.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return new DateTimeOffset(DateTime.Today).ToUniversalTime();
        }

        if (DateOnly.TryParseExact(since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)).ToUniversalTime();
        }

        throw new AppException($"--since expects YYYY-MM-DD or 'today', got '{since}'.");
    }

    private static string Percent(WinRate rate) => rate.Percentage is { } p ? (p * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-";

    private static string Mode(string? gameType) => gameType switch
    {
        null => "?",
        _ when gameType.StartsWith("GT_", StringComparison.Ordinal) => gameType[3..].ToLowerInvariant().Replace('_', ' '),
        _ => gameType,
    };

    private static string Format(string? format) => format switch
    {
        "FT_STANDARD" => "standard",
        "FT_WILD" => "wild",
        "FT_CLASSIC" => "classic",
        "FT_TWIST" => "twist",
        null => "?",
        _ => format,
    };

    private static string Title(string? value) => string.IsNullOrEmpty(value) ? "?" : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
}

