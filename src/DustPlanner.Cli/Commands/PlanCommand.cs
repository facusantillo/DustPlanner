using System.CommandLine;
using DustPlanner.App;
using DustPlanner.App.Reports;
using DustPlanner.Cli.Rendering;
using DustPlanner.Store;

namespace DustPlanner.Cli.Commands;

internal static class PlanCommand
{
    public static Command Build(Func<ParseResult, CliContext> context)
    {
        var deckArg = new Argument<string>("deck") { Description = "Target deck: code, a file with the pasted block, or '-' for stdin." };
        var collectionOpt = new Option<string?>("--collection", "-c") { Description = "Collection file (DustPlanner export, HSReplay JSON or CSV). Default: latest exporter inbox." };
        var accountOpt = new Option<string?>("--account", "-a") { Description = "Account key or BattleTag when the inbox holds several accounts." };
        var dustOpt = new Option<int?>("--dust") { Description = "Override the dust balance of the snapshot." };
        var needOpt = new Option<int?>("--need") { Description = "Plan for this much dust instead of the deck's shortfall." };
        var decksOpt = new Option<string?>("--decks") { Description = "decks.json to protect (default: the account's exporter inbox)." };
        var protectOpt = new Option<string[]>("--protect-deck") { Description = "Extra deck code(s) to protect. Repeatable.", AllowMultipleArgumentsPerToken = true };
        var prefsOpt = new Option<string?>("--prefs") { Description = "prefs.json (default: <home>/prefs.json, else built-in defaults)." };
        var metaOpt = new Option<string?>("--meta") { Description = "meta.txt with pasted meta deck codes (default: <home>/meta.txt)." };
        var refundsOpt = new Option<string?>("--refunds") { Description = "refunds.json with active nerf refund windows (default: <home>/refunds.json)." };
        var allowNoDecksOpt = new Option<bool>("--allow-no-decks") { Description = "Plan even when no saved decks are known (only the target deck is protected)." };
        var noSaveOpt = new Option<bool>("--no-save") { Description = "Do not store the plan in the local database (stored plans are matched against later snapshots by 'sync')." };
        var localeOpt = new Option<string>("--locale") { Description = "Card-name locale.", DefaultValueFactory = _ => "enUS" };
        var asOfOpt = new Option<DateOnly?>("--as-of") { Description = "Date for Standard legality, rotation and refund windows (default: today)." };

        var command = new Command("plan", "Missing cards for a deck plus the least painful disenchant plan to afford them.")
        {
            deckArg, collectionOpt, accountOpt, dustOpt, needOpt, decksOpt, protectOpt, prefsOpt, metaOpt, refundsOpt, allowNoDecksOpt, noSaveOpt, localeOpt, asOfOpt,
        };

        command.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            var outcome = await ctx.App.PlanAsync(new PlanOptions(CliContext.DeckText(result.GetRequiredValue(deckArg)))
            {
                CollectionFile = result.GetValue(collectionOpt),
                Account = result.GetValue(accountOpt),
                DustOverride = result.GetValue(dustOpt),
                Locale = result.GetValue(localeOpt) ?? "enUS",
                AsOf = result.GetValue(asOfOpt),
                NeedOverride = result.GetValue(needOpt),
                DecksFile = result.GetValue(decksOpt),
                ProtectDeckCodes = result.GetValue(protectOpt) ?? Array.Empty<string>(),
                PrefsFile = result.GetValue(prefsOpt),
                MetaFile = result.GetValue(metaOpt),
                RefundsFile = result.GetValue(refundsOpt),
                AllowNoDecks = result.GetValue(allowNoDecksOpt),
            }, ct).ConfigureAwait(false);

            if (outcome.Need.Catalog.Warning is not null && !ctx.Json)
            {
                await ctx.Err.WriteLineAsync("warning: " + outcome.Need.Catalog.Warning).ConfigureAwait(false);
            }

            var plan = outcome.Plan;
            var need = outcome.Need;
            var json = PlanJson.From(outcome);
            long? savedId = null;
            if (plan.HasPlan && !result.GetValue(noSaveOpt))
            {
                using var store = DustStore.Open(ctx.Paths.Database);
                new SnapshotRepository(store).UpsertAccount(need.Collection.AccountKey, need.Collection.BattleTag, need.Collection.Region, need.Collection.TakenAt);
                savedId = new PlanRepository(store).Save(need.Collection.AccountKey, need.Deck, plan, json, DateTimeOffset.UtcNow);
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(json with { SavedPlanId = savedId }).ConfigureAwait(false);
            }
            else
            {
                await ctx.Out.WriteAsync(PlanRenderer.Render(plan, need.Report, need.Collection, need.Catalog.Catalog)).ConfigureAwait(false);
                if (savedId is { } id)
                {
                    await ctx.Out.WriteLineAsync($"Plan #{id} stored; 'dustplanner sync' after disenchanting will match it against your next export.").ConfigureAwait(false);
                }
            }

            return plan.HasPlan || plan.Need <= 0 ? 0 : 1;
        }));

        return command;
    }
}
