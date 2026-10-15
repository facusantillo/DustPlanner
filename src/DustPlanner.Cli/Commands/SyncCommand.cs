using System.CommandLine;
using DustPlanner.App;
using DustPlanner.App.Reports;
using System.Globalization;
using System.Text.Json;
using DustPlanner.Cli.Rendering;
using DustPlanner.Contracts;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Json;
using DustPlanner.Core.Planner;
using DustPlanner.Store;

namespace DustPlanner.Cli.Commands;

internal static class SyncCommand
{
    public static Command Build(Func<ParseResult, CliContext> context)
    {
        var accountOpt = new Option<string?>("--account", "-a") { Description = "Only this account (key or BattleTag). Default: every account in the inbox." };
        var collectionOpt = new Option<string?>("--collection", "-c") { Description = "Import this collection file instead of the inbox (HDT export, HSReplay JSON or CSV)." };
        var refundsOpt = new Option<string?>("--refunds") { Description = "refunds.json used to estimate dust of detected disenchants (default: <home>/refunds.json)." };

        var command = new Command("sync", "Import the exporter inbox (collection, decks, rank) into the local database and record what changed since the last snapshot.")
        {
            accountOpt, collectionOpt, refundsOpt,
        };

        command.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            var catalog = (await ctx.LoadCatalogAsync(forceRefresh: false, "enUS", ct).ConfigureAwait(false)).Catalog;
            var refunds = Refunds.LoadOrEmpty(result.GetValue(refundsOpt) ?? Path.Combine(ctx.Paths.Config, Refunds.FileName));
            using var store = DustStore.Open(ctx.Paths.Database);
            var history = new HistoryService(store);
            var decks = new DeckRepository(store);
            var ranks = new RankRepository(store);
            var now = DateTimeOffset.UtcNow;
            var summaries = new List<SyncSummary>();

            var explicitFile = result.GetValue(collectionOpt);
            if (explicitFile is not null)
            {
                var collection = ctx.LoadCollection(explicitFile, null, null, catalog);
                summaries.Add(Summarize(history.Sync(collection, catalog, ctx.Rules, refunds, CliContext.Today, now), collection, null, null, catalog));
            }
            else
            {
                var accounts = InboxLocator.ListAccounts(ctx.Paths.Inbox);
                var requested = result.GetValue(accountOpt);
                if (requested is not null)
                {
                    var (selected, reason) = InboxLocator.Select(accounts, requested);
                    if (selected is null)
                    {
                        throw new AppException(reason ?? "Account not found.");
                    }

                    accounts = new[] { selected };
                }

                if (accounts.Count == 0)
                {
                    throw new AppException($"No exports in {ctx.Paths.Inbox}. Install the HDT exporter plugin (docs/exporter-install.md) or pass --collection <file>.");
                }

                foreach (var account in accounts)
                {
                    var collection = HdtExportImporter.ImportFile(Path.Combine(account.Directory, ExportSchema.CollectionFileName), ctx.Rules.PremiumSlots);
                    var sync = history.Sync(collection, catalog, ctx.Rules, refunds, CliContext.Today, now);

                    int? deckCount = null;
                    var decksFile = Path.Combine(account.Directory, ExportSchema.DecksFileName);
                    if (File.Exists(decksFile))
                    {
                        var export = JsonSerializer.Deserialize<DecksExport>(File.ReadAllText(decksFile), CoreJson.Options);
                        if (export is not null)
                        {
                            deckCount = decks.ImportExport(export);
                        }
                    }

                    int? rankCount = null;
                    var rankFile = Path.Combine(account.Directory, ExportSchema.RankFileName);
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
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(summaries).ConfigureAwait(false);
                return 0;
            }

            foreach (var s in summaries)
            {
                await ctx.Out.WriteLineAsync($"{s.Account}: snapshot {s.SnapshotTakenAt.ToLocalTime():yyyy-MM-dd HH:mm} {(s.NewSnapshot ? "imported" : "already known")} · dust {s.Dust.ToString("N0", CultureInfo.InvariantCulture)} ({(s.DustDelta >= 0 ? "+" : string.Empty)}{s.DustDelta.ToString("N0", CultureInfo.InvariantCulture)})"
                                            + (s.DecksImported is { } d ? $" · {d} decks" : string.Empty) + (s.RankSamples is { } r ? $" · {r} rank sample(s)" : string.Empty)).ConfigureAwait(false);
                if (s.Events.Count == 0)
                {
                    await ctx.Out.WriteLineAsync(s.NewSnapshot ? "  no card changes since the previous snapshot" : "  nothing new").ConfigureAwait(false);
                    continue;
                }

                var table = new TextTable().AddColumn("change").AddColumn("card").AddColumn("kind").AddColumn("copies", true).AddColumn("dust est.", true).AddColumn("plan").AddColumn("confidence");
                foreach (var e in s.Events)
                {
                    table.AddRow(e.EventKind, e.CardName, e.Kind, Math.Abs(e.Delta), e.DustEstimate, e.PlanId is { } p ? "#" + p.ToString(CultureInfo.InvariantCulture) : "-", e.Confidence);
                }

                await ctx.Out.WriteAsync(table.Render()).ConfigureAwait(false);
            }

            return 0;
        }));

        return command;
    }

    private static SyncSummary Summarize(SyncResult sync, CollectionSnapshot collection, int? deckCount, int? rankCount, Core.Cards.CardCatalog catalog) => new(
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

internal sealed record SyncSummary(string Account, string AccountKey, DateTimeOffset SnapshotTakenAt, bool NewSnapshot, int Dust, int DustDelta, int? DecksImported, int? RankSamples, IReadOnlyList<EventJson> Events);
