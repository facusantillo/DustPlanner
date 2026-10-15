using System.CommandLine;
using DustPlanner.App;
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

        var cardArg = new Argument<string>("card") { Description = "Card name (as the game shows it) or dbf id." };
        var goldenOpt = new Option<bool>("--golden") { Description = "The golden copy (default: the regular copy)." };
        var block = new Command("block", "Record that the game shows this copy as 'Uncraftable' (cannot be disenchanted): it is never proposed again. Written to <home>/data/non-craftable.json.") { cardArg, goldenOpt };
        block.SetAction(CliAction.Guarded((result, ct) => BlockAsync(context(result), result.GetRequiredValue(cardArg), result.GetValue(goldenOpt), uncraftable: true, ct)));
        var unblock = new Command("unblock", "The opposite of 'block': the game lets you disenchant this copy although the data says otherwise.") { cardArg, goldenOpt };
        unblock.SetAction(CliAction.Guarded((result, ct) => BlockAsync(context(result), result.GetRequiredValue(cardArg), result.GetValue(goldenOpt), uncraftable: false, ct)));

        return new Command("collection", "Imported collection snapshots.") { list, show, block, unblock };
    }

    private static async Task<int> BlockAsync(CliContext ctx, string cardArgument, bool golden, bool uncraftable, CancellationToken ct)
    {
        var catalog = (await ctx.LoadCatalogAsync(forceRefresh: false, "enUS", ct).ConfigureAwait(false)).Catalog;
        Core.Cards.CardDef card;
        if (int.TryParse(cardArgument, NumberStyles.None, CultureInfo.InvariantCulture, out var dbfId))
        {
            card = catalog.GetByDbfId(dbfId) ?? throw new AppException($"No card with dbf id {dbfId} in catalog build {catalog.Build}.");
        }
        else
        {
            // Same name in a set that is uncraftable anyway (Ysera in Classic format, Sneed's Old Shredder in Core) is never meant.
            var craftability = ctx.Rules.Craftability;
            List<Core.Cards.CardDef> Prefer(List<Core.Cards.CardDef> cards)
            {
                var craftableSets = cards.Where(c => !craftability.IsSetNonCraftable(c.Set)).ToList();
                return craftableSets.Count > 0 ? craftableSets : cards;
            }

            var matches = Prefer(catalog.FindByName(cardArgument).Where(IsBlockable).ToList());
            if (matches.Count == 0)
            {
                // No exact name: accept a unique partial name ("marin the" → "Marin the Fox"), otherwise suggest.
                var partial = Prefer(catalog.All.Where(c => IsBlockable(c) && c.Name.Contains(cardArgument.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.DbfId).ToList());
                var names = partial.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count == 1)
                {
                    matches = partial;
                }
                else if (names.Count > 1)
                {
                    throw new AppException($"No card named '{cardArgument}'; did you mean: " + string.Join(", ", partial.Take(12).Select(m => $"{m.Name} [{m.Set}] = {m.DbfId}")) + (partial.Count > 12 ? ", …" : "") + "? Pass the full name or the dbf id.");
                }
            }

            card = matches.Count switch
            {
                1 => matches[0],
                0 => throw new AppException($"No collectible card named '{cardArgument}' in catalog build {catalog.Build} (names are case-insensitive; a unique part of the name is enough)."),
                _ => throw new AppException($"'{cardArgument}' matches several cards; pass the dbf id instead: " + string.Join(", ", matches.Select(m => $"{m.Name} [{m.Set}] = {m.DbfId}"))),
            };
        }

        var kind = golden ? PremiumKind.Golden : PremiumKind.Normal;
        var path = Craftability.WriteOverride(ctx.Paths.DataOverrides, card.DbfId, kind, uncraftable);
        if (ctx.Json)
        {
            await ctx.WriteJsonAsync(new { dbf_id = card.DbfId, name = card.Name, kind = kind.ToString().ToLowerInvariant(), uncraftable, file = path }).ConfigureAwait(false);
            return 0;
        }

        await ctx.Out.WriteLineAsync($"{card.Name} ({kind.ToString().ToLowerInvariant()}, {card.Set}) recorded as {(uncraftable ? "uncraftable: never proposed" : "craftable: the data rule is overridden")} in {path}").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Cards a player can hold copies of: hero skins and the hidden Core variants are not blockable.</summary>
    private static bool IsBlockable(Core.Cards.CardDef card) => card.Set != "HERO_SKINS" && card.Set != "CORE_HIDDEN";
}
