using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DustPlanner.App;
using DustPlanner.App.Reports;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Rules;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Json;
using DustPlanner.Core.Planner;
using DustPlanner.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DustPlanner.Host.Api;

/// <summary>
/// The JSON API behind the panel (and anything else on this machine): the same operations as the CLI, the same
/// snake_case payloads as <c>--json</c>. Loopback only. The only writes are the ones the CLI does too — the local
/// database, <c>prefs.json</c>, <c>meta.txt</c>, the per-card override file and the catalog cache. Never the game.
/// </summary>
internal static class ApiEndpoints
{
    private static readonly JsonSerializerOptions Json = CoreJson.Options;

    public static void Map(WebApplication app, ServiceInfo info)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/service", () => Ok(info));

        api.MapGet("/overview", async (AppServices services, OverlayStateService state, string? account, CancellationToken ct) =>
            await Guard(async () => await OverviewBuilder.BuildAsync(services, state, info, account, ct).ConfigureAwait(false)).ConfigureAwait(false));

        api.MapGet("/decks", async (AppServices services, string? account, CancellationToken ct) =>
            await Guard(async () =>
            {
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                return (object)new { decks = services.ListDecks(account, catalog.Catalog) };
            }).ConfigureAwait(false));

        api.MapPost("/need", async (HttpContext http, AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var request = await Body<NeedRequest>(http, ct).ConfigureAwait(false);
                var outcome = await services.NeedAsync(new NeedOptions(RequireDeck(request.Deck)) { Account = request.Account, AsOf = ParseDate(request.AsOf), Locale = request.Locale ?? "enUS" }, ct).ConfigureAwait(false);
                return (object)new { report = MissingReportJson.From(outcome.Report, outcome.Collection, outcome.Catalog.Catalog), catalog_warning = outcome.Catalog.Warning };
            }).ConfigureAwait(false));

        api.MapPost("/plan", async (HttpContext http, AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var request = await Body<PlanRequest>(http, ct).ConfigureAwait(false);
                var outcome = await services.PlanAsync(new PlanOptions(RequireDeck(request.Deck))
                {
                    Account = request.Account,
                    AsOf = ParseDate(request.AsOf),
                    Locale = request.Locale ?? "enUS",
                    NeedOverride = request.Need,
                    ProtectDeckCodes = request.ProtectDeckCodes ?? Array.Empty<string>(),
                    AllowNoDecks = request.AllowNoDecks,
                }, ct).ConfigureAwait(false);
                var json = PlanJson.From(outcome);
                long? savedId = null;
                if (request.Save && outcome.Plan.HasPlan)
                {
                    savedId = services.SavePlan(outcome.Need, outcome.Plan, json, DateTimeOffset.UtcNow);
                }

                return (object)new { plan = json with { SavedPlanId = savedId }, catalog_warning = outcome.Need.Catalog.Warning };
            }).ConfigureAwait(false));

        api.MapPost("/sync", async (HttpContext http, AppServices services, OverlayStateService state, CancellationToken ct) =>
            await Guard(async () =>
            {
                var request = http.Request.ContentLength > 0 ? await Body<SyncRequest>(http, ct).ConfigureAwait(false) : new SyncRequest(null);
                var outcome = state.RunSync(ApiEndpoints.ResolveAccount(services, request.Account));
                return (object)new { sync = outcome.State, accounts = outcome.Accounts };
            }).ConfigureAwait(false));

        api.MapGet("/history", async (AppServices services, string? account, int? limit, CancellationToken ct) =>
            await Guard(async () =>
            {
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                return History(services, catalog.Catalog, account, limit ?? 50);
            }).ConfigureAwait(false));

        api.MapGet("/games", (AppServices services, string? account, string? since, int? days, string? type, string? format, int? recent) =>
            Guard(() => Task.FromResult<object>(services.GamesOverview(account, AppServices.ParseSince(since, days), type, format, recent ?? 20))));

        api.MapPost("/games/import", async (HttpContext http, AppServices services, OverlayHostOptions options, CancellationToken ct) =>
            await Guard(async () =>
            {
                var request = http.Request.ContentLength > 0 ? await Body<GamesImportRequest>(http, ct).ConfigureAwait(false) : new GamesImportRequest(null);
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                // The service's own --logs folder is the default, then HDT's config / the usual install paths.
                return (object)services.ImportGames(request.Logs ?? options.LogsDirectory, catalog.Catalog);
            }).ConfigureAwait(false));

        api.MapGet("/prefs", (AppServices services) => Guard(() => Task.FromResult<object>(new { path = services.PrefsPath, exists = File.Exists(services.PrefsPath), prefs = services.ReadPrefs() })));

        api.MapPut("/prefs", async (HttpContext http, AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                using var reader = new StreamReader(http.Request.Body);
                var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                return (object)new { path = services.PrefsPath, exists = true, prefs = services.WritePrefs(text) };
            }).ConfigureAwait(false));

        api.MapGet("/meta", async (AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                return (object)services.ReadMeta(catalog.Catalog);
            }).ConfigureAwait(false));

        api.MapPut("/meta", async (HttpContext http, AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var request = await Body<MetaRequest>(http, ct).ConfigureAwait(false);
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                return (object)services.WriteMeta(request.Text ?? string.Empty, catalog.Catalog);
            }).ConfigureAwait(false));

        api.MapGet("/cards/search", async (AppServices services, string? q, int? limit, CancellationToken ct) =>
            await Guard(async () =>
            {
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                var cards = AppServices.SearchCards(q ?? string.Empty, catalog.Catalog, limit ?? 20)
                    .Select(c => new CardSummaryJson(c.DbfId, c.CardId, c.Name, c.Rarity, c.CardClass, c.Set, c.Type)).ToList();
                return (object)new { cards };
            }).ConfigureAwait(false));

        api.MapGet("/cards/overrides", async (AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                return (object)new { overrides = services.ListCardOverrides(catalog.Catalog) };
            }).ConfigureAwait(false));

        api.MapPost("/cards/overrides", async (HttpContext http, AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var request = await Body<CardOverrideRequest>(http, ct).ConfigureAwait(false);
                var catalog = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                var card = services.ResolveCard(RequireText(request.Card, "card"), catalog.Catalog);
                return (object)services.SetCardOverride(card, request.Golden ? PremiumKind.Golden : PremiumKind.Normal, request.Uncraftable);
            }).ConfigureAwait(false));

        api.MapDelete("/cards/overrides/{dbfId:int}", (AppServices services, int dbfId, bool? golden) =>
            Guard(() => Task.FromResult<object>(new { dbf_id = dbfId, removed = services.ClearCardOverride(dbfId, golden == true ? PremiumKind.Golden : PremiumKind.Normal) })));

        api.MapGet("/catalog", async (AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var loaded = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
                return (object)new { build = loaded.Catalog.Build, locale = loaded.Catalog.Locale, cards = loaded.Catalog.Count, from_cache = loaded.FromCache, cached_at = loaded.CachedAt, warning = loaded.Warning };
            }).ConfigureAwait(false));

        api.MapPost("/catalog/update", async (AppServices services, CancellationToken ct) =>
            await Guard(async () =>
            {
                var loaded = await services.LoadCatalogAsync(forceRefresh: true, "enUS", ct).ConfigureAwait(false);
                services.InvalidateCatalogCache();
                return (object)new { build = loaded.Catalog.Build, locale = loaded.Catalog.Locale, cards = loaded.Catalog.Count, from_cache = loaded.FromCache, cached_at = loaded.CachedAt, warning = loaded.Warning };
            }).ConfigureAwait(false));
    }

    private static object History(AppServices services, CardCatalog catalog, string? account, int limit)
    {
        if (!File.Exists(services.Paths.Database))
        {
            return new { account_key = (string?)null, known_accounts = Array.Empty<string>(), snapshots = Array.Empty<StoredSnapshot>(), plans = Array.Empty<StoredPlan>(), events = Array.Empty<EventJson>() };
        }

        using var store = DustStore.Open(services.Paths.Database, readOnly: true);
        var snapshots = new SnapshotRepository(store);
        var known = snapshots.Accounts();
        var accountKey = ResolveAccount(services, account) ?? (known.Count > 0 ? known[0] : null);
        if (accountKey is null)
        {
            return new { account_key = (string?)null, known_accounts = known, snapshots = Array.Empty<StoredSnapshot>(), plans = Array.Empty<StoredPlan>(), events = Array.Empty<EventJson>() };
        }

        var plans = new PlanRepository(store);
        return new
        {
            account_key = accountKey,
            known_accounts = known,
            snapshots = snapshots.List(accountKey, 20),
            plans = plans.List(accountKey, 50).Select(p => new { plan = p, picks = plans.Picks(p.Id).Select(k => new { k.DbfId, name = catalog.GetByDbfId(k.DbfId)?.Name ?? $"dbfId {k.DbfId}", kind = PlanRepository.KindName(k.Kind), k.Copies, k.DustEach, k.PainEach, k.MatchedCopies }).ToList() }).ToList(),
            events = new HistoryService(store).Events(accountKey, Math.Clamp(limit, 1, 500)).Select(e => EventJson.From(e, catalog)).ToList(),
        };
    }

    /// <summary>Account key or BattleTag (prefix) → account key, through the inbox; null when nothing matches or nothing was given.</summary>
    internal static string? ResolveAccount(AppServices services, string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return null;
        }

        var (selected, _) = InboxLocator.Select(InboxLocator.ListAccounts(services.Paths.Inbox), account, allowMostRecent: false);
        return selected?.AccountKey ?? account;
    }

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

    private static string RequireDeck(string? deck) => RequireText(deck, "deck");

    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new AppException($"'{field}' is required.") : value;

    private static async Task<T> Body<T>(HttpContext http, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json, ct).ConfigureAwait(false)
                ?? throw new AppException("Request body is empty.");
        }
        catch (JsonException ex)
        {
            throw new AppException("Invalid JSON body: " + ex.Message);
        }
    }

    private static IResult Ok(object payload) => Results.Content(JsonSerializer.Serialize(payload, Json), "application/json; charset=utf-8");

    private static IResult Error(int status, string message, string? type = null) =>
        Results.Content(JsonSerializer.Serialize(new { error = message, type }, Json), "application/json; charset=utf-8", statusCode: status);

    /// <summary>User-facing failures become 400 with the message; anything else 500 with the exception type, never a stack trace.</summary>
    private static async Task<IResult> Guard(Func<Task<object>> body)
    {
        try
        {
            return Ok(await body().ConfigureAwait(false));
        }
        catch (AppException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidDeckstringException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "Invalid deck code: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(StatusCodes.Status499ClientClosedRequest, "cancelled");
        }
#pragma warning disable CA1031 // the panel must always get an answer it can show
        catch (Exception ex)
        {
            return Error(StatusCodes.Status500InternalServerError, ex.Message, ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    public static ServiceInfo DescribeService(string url, string home) => new(
        "dustplanner-host",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        url,
        home,
        Environment.ProcessId,
        DateTimeOffset.UtcNow);
}
