using DustPlanner.App;
using DustPlanner.App.Reports;
using DustPlanner.Contracts;
using DustPlanner.Core.Collection;
using DustPlanner.LogTracker;
using DustPlanner.Store;

namespace DustPlanner.Host.Api;

/// <summary>Everything the panel's home page shows in one answer.</summary>
internal sealed record OverviewJson(
    ServiceInfo Service,
    CatalogInfoJson Catalog,
    IReadOnlyList<AccountJson> Accounts,
    AccountJson? Account,
    CollectionSummaryJson? Collection,
    ExportInfoJson? Export,
    RankState? Rank,
    SyncState? LastSync,
    StatsJson Today,
    StatsJson Week,
    RecordState Session,
    CurrentGameState? CurrentGame,
    LastGameState? LastGame,
    IReadOnlyList<StoredPlan> OpenPlans,
    int DeckCount,
    int MetaDeckCount,
    IReadOnlyList<string> Warnings);

internal sealed record CatalogInfoJson(string Build, string Locale, int Cards, string? Warning);

internal sealed record AccountJson(string AccountKey, string? BattleTag, string? Region, DateTimeOffset? CollectionExportedAt, bool HasDecks, bool HasRank);

internal sealed record ExportInfoJson(DateTimeOffset ExportedAt, double AgeMinutes, string Directory);

internal static class OverviewBuilder
{
    public static async Task<object> BuildAsync(AppServices services, OverlayStateService state, ServiceInfo info, string? account, CancellationToken ct)
    {
        var loaded = await services.CachedCatalogAsync("enUS", ct).ConfigureAwait(false);
        var catalog = loaded.Catalog;
        var warnings = new List<string>(state.Current.Warnings);

        var inbox = InboxLocator.ListAccounts(services.Paths.Inbox);
        var accounts = inbox.Select(a => new AccountJson(a.AccountKey, a.BattleTag, a.Region, a.CollectionExportedAt, a.HasDecks, a.HasRank)).ToList();
        var (selected, _) = InboxLocator.Select(inbox, account);

        AccountJson? current = null;
        CollectionSummaryJson? collection = null;
        ExportInfoJson? export = null;
        var deckCount = 0;
        if (selected is not null)
        {
            current = new AccountJson(selected.AccountKey, selected.BattleTag, selected.Region, selected.CollectionExportedAt, selected.HasDecks, selected.HasRank);
            try
            {
                var snapshot = HdtExportImporter.ImportFile(Path.Combine(selected.Directory, ExportSchema.CollectionFileName), services.Rules.PremiumSlots);
                collection = CollectionSummaryJson.From(snapshot, catalog, services.Rules);
                export = new ExportInfoJson(snapshot.TakenAt, Math.Round((DateTimeOffset.UtcNow - snapshot.TakenAt).TotalMinutes, 1), selected.Directory);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is System.Text.Json.JsonException)
            {
                warnings.Add("The collection export could not be read: " + ex.Message);
            }

            deckCount = services.ListDecks(selected.AccountKey, catalog).Count(d => !d.Archived);
        }
        else
        {
            warnings.Add("No collection export yet: open the game's Collection once with the DustPlanner plugin enabled in HDT.");
        }

        var accountKey = selected?.AccountKey ?? state.Current.AccountKey;
        var today = StatsJson.From(new WinRate(0, 0, 0, 0));
        var week = today;
        IReadOnlyList<StoredPlan> openPlans = Array.Empty<StoredPlan>();
        if (File.Exists(services.Paths.Database) && accountKey is not null)
        {
            using var store = DustStore.Open(services.Paths.Database, readOnly: true);
            var games = new GameRepository(store);
            today = StatsJson.From(games.Stats(accountKey, AppServices.ParseSince("today", null)));
            week = StatsJson.From(games.Stats(accountKey, AppServices.ParseSince(null, 7)));
            openPlans = new PlanRepository(store).List(accountKey, 20).Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Partial).Take(5).ToList();
        }

        var metaDecks = 0;
        try
        {
            metaDecks = services.ReadMeta(catalog).DeckCount;
        }
        catch (IOException)
        {
            // meta.txt unreadable: the planner would ignore it too
        }

        if (metaDecks == 0)
        {
            warnings.Add("No meta decks (meta.txt): Standard legendaries and epics are not protected by the meta signal. Paste a few competitive deck codes in Settings.");
        }

        var live = state.Current;
        return new OverviewJson(
            info,
            new CatalogInfoJson(catalog.Build, catalog.Locale, catalog.Count, loaded.Warning),
            accounts,
            current,
            collection,
            export,
            live.Rank,
            live.LastSync,
            today,
            week,
            live.Session,
            live.CurrentGame,
            live.LastGame,
            openPlans,
            deckCount,
            metaDecks,
            warnings.Distinct(StringComparer.Ordinal).ToList());
    }
}
