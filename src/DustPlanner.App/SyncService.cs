using System.Text.Json;
using DustPlanner.App.Reports;
using DustPlanner.Contracts;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Json;
using DustPlanner.Core.Planner;
using DustPlanner.Store;

namespace DustPlanner.App;

/// <summary>Outcome of importing one account's export (what <c>sync</c> prints and the panel shows).</summary>
public sealed record SyncSummary(string Account, string AccountKey, DateTimeOffset SnapshotTakenAt, bool NewSnapshot, int Dust, int DustDelta, int? DecksImported, int? RankSamples, IReadOnlyList<EventJson> Events);

public sealed partial class AppServices
{
    /// <summary>
    /// Imports the exporter inbox (collection, decks, rank) into the database and records what changed since the
    /// previous snapshot; stored plans are matched against the changes. Idempotent: an export already imported is
    /// reported as "already known". <paramref name="collectionFile"/> imports that file instead of the inbox.
    /// </summary>
    public IReadOnlyList<SyncSummary> SyncInbox(CardCatalog catalog, string? account, string? collectionFile, string? refundsFile, DateOnly asOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var refunds = LoadRefunds(refundsFile);
        Directory.CreateDirectory(Paths.Root);
        using var store = DustStore.Open(Paths.Database);
        var history = new HistoryService(store);
        var decks = new DeckRepository(store);
        var ranks = new RankRepository(store);
        var summaries = new List<SyncSummary>();

        if (collectionFile is not null)
        {
            var collection = LoadCollection(collectionFile, null, null, catalog);
            summaries.Add(Summarize(history.Sync(collection, catalog, Rules, refunds, asOf, now), collection, null, null, catalog));
            return summaries;
        }

        var accounts = InboxLocator.ListAccounts(Paths.Inbox);
        if (account is not null)
        {
            var (selected, reason) = InboxLocator.Select(accounts, account);
            if (selected is null)
            {
                throw new AppException(reason ?? "Account not found.");
            }

            accounts = new[] { selected };
        }

        if (accounts.Count == 0)
        {
            throw new AppException($"No exports in {Paths.Inbox}. Install the HDT exporter plugin (docs/exporter-install.md) or pass a collection file.");
        }

        foreach (var inboxAccount in accounts)
        {
            var collection = HdtExportImporter.ImportFile(Path.Combine(inboxAccount.Directory, ExportSchema.CollectionFileName), Rules.PremiumSlots);
            var sync = history.Sync(collection, catalog, Rules, refunds, asOf, now);

            int? deckCount = null;
            var decksFile = Path.Combine(inboxAccount.Directory, ExportSchema.DecksFileName);
            if (File.Exists(decksFile))
            {
                var export = JsonSerializer.Deserialize<DecksExport>(File.ReadAllText(decksFile), CoreJson.Options);
                if (export is not null)
                {
                    deckCount = decks.ImportExport(export);
                }
            }

            int? rankCount = null;
            var rankFile = Path.Combine(inboxAccount.Directory, ExportSchema.RankFileName);
            if (File.Exists(rankFile))
            {
                var export = JsonSerializer.Deserialize<RankExport>(File.ReadAllText(rankFile), CoreJson.Options);
                if (export is not null)
                {
                    rankCount = ranks.Import(export);
                }
            }

            summaries.Add(Summarize(sync, collection, deckCount, rankCount, catalog));
        }

        return summaries;
    }

    /// <summary>Stores a plan so later syncs can match disenchants against it. Returns the plan id.</summary>
    public long SavePlan(NeedOutcome need, DisenchantPlan plan, PlanJson payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(need);
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(Paths.Root);
        using var store = DustStore.Open(Paths.Database);
        new SnapshotRepository(store).UpsertAccount(need.Collection.AccountKey, need.Collection.BattleTag, need.Collection.Region, need.Collection.TakenAt);
        return new PlanRepository(store).Save(need.Collection.AccountKey, need.Deck, plan, payload, now);
    }

    private static SyncSummary Summarize(SyncResult sync, CollectionSnapshot collection, int? deckCount, int? rankCount, CardCatalog catalog) => new(
        collection.BattleTag ?? collection.AccountKey,
        collection.AccountKey,
        sync.Snapshot.TakenAt,
        sync.NewSnapshot,
        collection.Dust,
        sync.DustDelta,
        deckCount,
        rankCount,
        sync.Events.Select(e => EventJson.From(e, catalog)).ToList());
}
