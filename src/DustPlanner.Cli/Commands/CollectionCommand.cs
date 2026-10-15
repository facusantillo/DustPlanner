using System.CommandLine;
using DustPlanner.App.Reports;
using System.Globalization;
using DustPlanner.Cli.Rendering;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Rules;

namespace DustPlanner.Cli.Commands;

internal static class CollectionCommand
{
    public static Command Build(Func<ParseResult, CliContext> context)
    {
        var list = new Command("list", "List the accounts the HDT exporter has written to the inbox.");
        list.SetAction(CliAction.Guarded(async (result, _) =>
        {
            var ctx = context(result);
            var accounts = InboxLocator.ListAccounts(ctx.Paths.Inbox);
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(accounts).ConfigureAwait(false);
                return 0;
            }

            if (accounts.Count == 0)
            {
                await ctx.Out.WriteLineAsync($"No exports in {ctx.Paths.Inbox}. Install the HDT exporter plugin (docs/VERIFICACION.md §A).").ConfigureAwait(false);
                return 0;
            }

            var table = new TextTable().AddColumn("account").AddColumn("region").AddColumn("key").AddColumn("collection").AddColumn("decks").AddColumn("rank").AddColumn("diag");
            foreach (var a in accounts)
            {
                table.AddRow(a.BattleTag ?? "?", a.Region ?? "?", a.AccountKey, a.CollectionExportedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?", a.HasDecks ? "yes" : "no", a.HasRank ? "yes" : "no", a.HasDiagnostics ? "yes" : "no");
            }

            await ctx.Out.WriteAsync(table.Render(string.Empty)).ConfigureAwait(false);
            return 0;
        }));

        var collectionOpt = new Option<string?>("--collection", "-c") { Description = "Collection file instead of the inbox." };
        var accountOpt = new Option<string?>("--account", "-a") { Description = "Account key or BattleTag." };
        var show = new Command("show", "Summary of a collection snapshot: dust, copies per premium kind, unknown-value copies.") { collectionOpt, accountOpt };
        show.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            var catalog = (await ctx.LoadCatalogAsync(forceRefresh: false, "enUS", ct).ConfigureAwait(false)).Catalog;
            var collection = ctx.LoadCollection(result.GetValue(collectionOpt), result.GetValue(accountOpt), null, catalog);

            var byKind = Enum.GetValues<PremiumKind>().ToDictionary(k => k, k => collection.Cards.Sum(c => c.Count(k)));
            var unknownSets = collection.Cards.Select(c => catalog.GetByDbfId(c.DbfId)?.Set).Where(s => s is not null && !ctx.Rules.Craftability.IsSetKnown(s)).Distinct().Order().ToList();
            var notInCatalog = collection.Cards.Count(c => catalog.GetByDbfId(c.DbfId) is null);

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(CollectionSummaryJson.From(collection, catalog, ctx.Rules)).ConfigureAwait(false);
                return 0;
            }

            await ctx.Out.WriteLineAsync($"Account:        {collection.BattleTag ?? collection.AccountKey} ({collection.Region ?? "?"}) · key {collection.AccountKey}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Snapshot:       {collection.TakenAt.ToLocalTime():yyyy-MM-dd HH:mm} via {collection.Source}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Dust:           {collection.Dust.ToString("N0", CultureInfo.InvariantCulture)}{(collection.Gold is { } g ? $" · gold {g.ToString("N0", CultureInfo.InvariantCulture)}" : string.Empty)}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Unopened packs: {(collection.UnopenedPacks?.ToString(CultureInfo.InvariantCulture) ?? "unknown (no HDT API exposes it)")}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Cards:          {collection.DistinctCards.ToString("N0", CultureInfo.InvariantCulture)} distinct · normal {byKind[PremiumKind.Normal]:N0} · golden {byKind[PremiumKind.Golden]:N0} · signature {byKind[PremiumKind.Signature]:N0} · diamond {byKind[PremiumKind.Diamond]:N0} · unknown kind {byKind[PremiumKind.Unknown]:N0}").ConfigureAwait(false);
            if (byKind[PremiumKind.Unknown] > 0)
            {
                await ctx.Out.WriteLineAsync("                copies of unknown kind come from HDT slots 2/3; confirm the mapping in data/premium-slots.json (VERIFICACION A3).").ConfigureAwait(false);
            }

            if (notInCatalog > 0)
            {
                await ctx.Out.WriteLineAsync($"⚠ {notInCatalog} card(s) in the collection are not in catalog build {catalog.Build}; run 'dustplanner catalog update'.").ConfigureAwait(false);
            }

            if (unknownSets.Count > 0)
            {
                await ctx.Out.WriteLineAsync("⚠ Sets with unknown craftability in your collection: " + string.Join(", ", unknownSets)).ConfigureAwait(false);
            }

            return 0;
        }));

        return new Command("collection", "Imported collection snapshots.") { list, show };
    }
}
