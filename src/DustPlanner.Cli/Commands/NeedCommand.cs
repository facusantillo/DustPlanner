using System.CommandLine;
using DustPlanner.App;
using DustPlanner.App.Reports;
using DustPlanner.Cli.Rendering;

namespace DustPlanner.Cli.Commands;

internal static class NeedCommand
{
    public static Command Build(Func<ParseResult, CliContext> context)
    {
        var deckArg = new Argument<string>("deck") { Description = "Deck code, a file containing the pasted deck block, or '-' for stdin." };
        var collectionOpt = new Option<string?>("--collection", "-c") { Description = "Collection file (DustPlanner export, HSReplay JSON or CSV). Default: latest exporter inbox." };
        var accountOpt = new Option<string?>("--account", "-a") { Description = "Account key or BattleTag when the inbox holds several accounts." };
        var dustOpt = new Option<int?>("--dust") { Description = "Override the dust balance of the snapshot." };
        var localeOpt = new Option<string>("--locale") { Description = "Card-name locale (enUS, esES, esMX, ...).", DefaultValueFactory = _ => "enUS" };
        var asOfOpt = new Option<DateOnly?>("--as-of") { Description = "Date used for Standard legality and rotation (default: today)." };

        var command = new Command("need", "Cards you are missing for a deck, their dust cost, and what rotates soon.")
        {
            deckArg, collectionOpt, accountOpt, dustOpt, localeOpt, asOfOpt,
        };

        command.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            var outcome = await ctx.App.NeedAsync(new NeedOptions(CliContext.DeckText(result.GetRequiredValue(deckArg)))
            {
                CollectionFile = result.GetValue(collectionOpt),
                Account = result.GetValue(accountOpt),
                DustOverride = result.GetValue(dustOpt),
                Locale = result.GetValue(localeOpt) ?? "enUS",
                AsOf = result.GetValue(asOfOpt),
            }, ct).ConfigureAwait(false);

            if (outcome.Catalog.Warning is not null && !ctx.Json)
            {
                await ctx.Err.WriteLineAsync("warning: " + outcome.Catalog.Warning).ConfigureAwait(false);
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(MissingReportJson.From(outcome.Report, outcome.Collection, outcome.Catalog.Catalog)).ConfigureAwait(false);
            }
            else
            {
                await ctx.Out.WriteAsync(MissingReportRenderer.Render(outcome.Report, outcome.Collection, outcome.Catalog.Catalog)).ConfigureAwait(false);
            }

            return 0;
        }));

        return command;
    }
}
