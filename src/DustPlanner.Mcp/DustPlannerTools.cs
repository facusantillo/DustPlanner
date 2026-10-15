using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DustPlanner.App;
using DustPlanner.App.Reports;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Json;
using DustPlanner.LogTracker;
using DustPlanner.Store;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DustPlanner.Mcp;

/// <summary>
/// The five read-only tools. Every result is the same snake_case JSON the CLI prints with <c>--json</c>; user-facing
/// failures come back as a tool error (<c>isError</c>) with the message, never as a protocol error.
/// </summary>
[McpServerToolType]
internal sealed class DustPlannerTools(ReadOnlyGateway gateway)
{
    private static readonly string[] SummaryNotes =
    [
        "unopened_packs is null: no HDT API exposes the pack count; ask the user to open packs before planning.",
        "copies.unknown are HDT premium slots 2/3 (signature/diamond, unconfirmed); they are never proposed for disenchanting.",
    ];

    private static readonly string[] RankFormats = ["standard", "wild"];

    private readonly ReadOnlyGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    [McpServerTool(Name = "get_collection_summary", ReadOnly = true, Idempotent = true),
     Description("Summary of the Hearthstone collection exported by the HDT plugin: dust, gold, copies per premium kind, snapshot time, " +
                 "and the list of exported accounts. Pass 'account' (account key or BattleTag) when several accounts were exported.")]
    public Task<CallToolResult> GetCollectionSummary(
        [Description("Account key ('<hi>-<lo>') or BattleTag; default: the most recently exported account.")] string? account = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var app = _gateway.App;
            var accounts = InboxLocator.ListAccounts(app.Paths.Inbox);
            var catalog = await _gateway.CatalogAsync("enUS", cancellationToken).ConfigureAwait(false);
            var collection = app.LoadCollection(null, account, null, catalog.Catalog);
            return new
            {
                accounts = accounts.Select(a => new { account = a.BattleTag ?? a.AccountKey, account_key = a.AccountKey, region = a.Region, collection_exported_at = a.CollectionExportedAt, has_decks = a.HasDecks, has_rank = a.HasRank }),
                collection = CollectionSummaryJson.From(collection, catalog.Catalog, app.Rules),
                catalog_warning = catalog.Warning,
                notes = SummaryNotes,
            };
        });

    [McpServerTool(Name = "get_missing_for_deck", ReadOnly = true, Idempotent = true),
     Description("Cards missing from the collection for a deck (deck code or pasted deck block), their craft cost, the dust shortfall, " +
                 "cards that cannot be crafted, and the deck's cards that leave Standard at the next rotation.")]
    public Task<CallToolResult> GetMissingForDeck(
        [Description("Deck code (AAECA...) or the full pasted deck block from the game.")] string deck,
        [Description("Account key or BattleTag; default: the most recently exported account.")] string? account = null,
        [Description("Date (YYYY-MM-DD) for Standard legality and rotation; default: today.")] string? asOf = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var outcome = await _gateway.App.NeedAsync(new NeedOptions(deck) { Account = account, AsOf = ParseDate(asOf) }, cancellationToken).ConfigureAwait(false);
            return new { report = MissingReportJson.From(outcome.Report, outcome.Collection, outcome.Catalog.Catalog), catalog_warning = outcome.Catalog.Warning };
        });

    [McpServerTool(Name = "propose_disenchant_plan", ReadOnly = true, Idempotent = true),
     Description("Missing cards for a deck plus the least painful set of copies to disenchant to afford them. A proposal only: nothing is " +
                 "disenchanted and nothing is stored (the CLI 'plan' command stores plans). Copies in the user's saved decks, in the target deck " +
                 "and in meta decks are protected; copies with an unknown dust value are excluded and listed under 'notable_exclusions'. " +
                 "Every plan starts with the pending-packs warning.")]
    public Task<CallToolResult> ProposeDisenchantPlan(
        [Description("Target deck: deck code or pasted deck block.")] string deck,
        [Description("Account key or BattleTag; default: the most recently exported account.")] string? account = null,
        [Description("Plan for this much dust instead of the deck's shortfall.")] int? need = null,
        [Description("Extra deck codes to protect (cards used by them are never proposed).")] string[]? protectDeckCodes = null,
        [Description("Plan even when no saved decks are known (only the target deck is protected). Default false: without decks the plan is refused.")] bool allowNoDecks = false,
        [Description("Date (YYYY-MM-DD) for Standard legality, rotation and refund windows; default: today.")] string? asOf = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var outcome = await _gateway.App.PlanAsync(new PlanOptions(deck)
            {
                Account = account,
                AsOf = ParseDate(asOf),
                NeedOverride = need,
                ProtectDeckCodes = protectDeckCodes ?? Array.Empty<string>(),
                AllowNoDecks = allowNoDecks,
            }, cancellationToken).ConfigureAwait(false);
            return new { plan = PlanJson.From(outcome), catalog_warning = outcome.Need.Catalog.Warning };
        });

    [McpServerTool(Name = "get_history", ReadOnly = true, Idempotent = true),
     Description("Stored collection snapshots, disenchant plans (status proposed/partial/confirmed) and the card changes detected between " +
                 "snapshots by 'dustplanner sync', newest first, plus the latest known ladder rank.")]
    public Task<CallToolResult> GetHistory(
        [Description("Account key; default: the most recently synced account.")] string? account = null,
        [Description("Maximum change events to return (default 50).")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var catalog = await _gateway.CatalogAsync("enUS", cancellationToken).ConfigureAwait(false);
            using var store = _gateway.OpenDatabase();
            var snapshots = new SnapshotRepository(store);
            var known = snapshots.Accounts();
            var accountKey = account ?? (known.Count > 0 ? known[0] : null)
                ?? throw new AppException("The database holds no collection snapshot yet. Run 'dustplanner sync' first.");
            var ranks = new RankRepository(store);
            var rank = RankFormats.Select(f => ranks.Latest(accountKey, f)).Where(r => r is not null).Select(r => new { r!.Format, r.TakenAt, label = RankRepository.Describe(r), r.LeagueId, r.StarLevel, r.Stars, r.LegendRank });
            return new
            {
                account_key = accountKey,
                known_accounts = known,
                snapshots = snapshots.List(accountKey, 10),
                plans = new PlanRepository(store).List(accountKey),
                events = new HistoryService(store).Events(accountKey, Math.Clamp(limit, 1, 500)).Select(e => EventJson.From(e, catalog.Catalog)),
                rank,
            };
        });

    [McpServerTool(Name = "get_winrate", ReadOnly = true, Idempotent = true),
     Description("Win rate from the games parsed out of Hearthstone's Power.log ('dustplanner games import'): overall, per class, and the " +
                 "most recent games. Filter by period (since / days), game type (GT_RANKED, GT_CASUAL, ...) and format (FT_STANDARD, FT_WILD).")]
    public Task<CallToolResult> GetWinrate(
        [Description("Account key; default: the account of the most recent game.")] string? account = null,
        [Description("Only games started on/after this local date (YYYY-MM-DD) or 'today'.")] string? since = null,
        [Description("Only the last N days.")] int? days = null,
        [Description("Game type filter, e.g. GT_RANKED.")] string? gameType = null,
        [Description("Format filter: FT_STANDARD or FT_WILD.")] string? format = null,
        [Description("How many recent games to include (default 10).")] int recent = 10,
        CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var store = _gateway.OpenDatabase();
            var games = new GameRepository(store);
            var accountKey = account ?? games.LatestAccount() ?? throw new AppException("No games stored yet. Run 'dustplanner games import' first.");
            var from = ParseSince(since, days);
            var overall = games.Stats(accountKey, from, gameType, format);
            var byClass = games.StatsByClass(accountKey, from, gameType);
            return Task.FromResult<object>(new
            {
                account_key = accountKey,
                known_accounts = games.Accounts(),
                since = from,
                game_type = gameType,
                format,
                overall = StatsJson.From(overall),
                by_class = byClass.Select(c => new { player_class = c.PlayerClass, stats = StatsJson.From(c.Rate) }),
                recent_games = games.List(accountKey, Math.Clamp(recent, 0, 100)),
                note = "win_rate = wins / (wins + losses); ties and games without a result count in 'games' only. recent_games ignores the filters.",
            });
        });

    internal static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new AppException($"Expected a date as YYYY-MM-DD, got '{value}'.");
    }

    internal static DateTimeOffset? ParseSince(string? since, int? days)
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

        var date = ParseDate(since)!.Value;
        return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)).ToUniversalTime();
    }

    /// <summary>Serializes the payload as text; user-facing failures become an <c>isError</c> result carrying the message.</summary>
    private static async Task<CallToolResult> RunAsync(Func<Task<object>> body)
    {
        try
        {
            var payload = await body().ConfigureAwait(false);
            return Text(JsonSerializer.Serialize(payload, CoreJson.Options), isError: false);
        }
        catch (AppException ex)
        {
            return Text(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is HttpRequestException || ex is InvalidOperationException || ex is JsonException)
        {
            return Text(ex.Message, isError: true);
        }
    }

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
