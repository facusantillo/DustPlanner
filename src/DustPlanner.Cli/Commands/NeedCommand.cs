using System.CommandLine;
using DustPlanner.Cli.Rendering;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;

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
            DeckCode deck;
            try
            {
                deck = CliContext.ReadDeck(result.GetRequiredValue(deckArg));
            }
            catch (InvalidDeckstringException ex)
            {
                throw new CliException("Invalid deck code: " + ex.Message);
            }

            var catalog = (await ctx.LoadCatalogAsync(forceRefresh: false, result.GetValue(localeOpt) ?? "enUS", ct).ConfigureAwait(false)).Catalog;
            var collection = ctx.LoadCollection(result.GetValue(collectionOpt), result.GetValue(accountOpt), result.GetValue(dustOpt), catalog);
            var report = MissingCards.Analyze(deck, collection, catalog, ctx.Rules, result.GetValue(asOfOpt) ?? CliContext.Today);

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(MissingReportJson.From(report, collection, catalog)).ConfigureAwait(false);
            }
            else
            {
                await ctx.Out.WriteAsync(MissingReportRenderer.Render(report, collection, catalog)).ConfigureAwait(false);
            }

            return 0;
        }));

        return command;
    }
}

/// <summary>JSON projection of <see cref="MissingReport"/> (keeps the CLI's output contract explicit and stable).</summary>
internal sealed record MissingReportJson(
    string? DeckName,
    string? DeckClass,
    string Format,
    int TotalCards,
    string Account,
    DateTimeOffset SnapshotAt,
    string SnapshotSource,
    string CatalogBuild,
    DateOnly AsOf,
    int DustAvailable,
    int TotalCraftCost,
    int Shortfall,
    bool Complete,
    IReadOnlyList<MissingCardJson> Missing,
    IReadOnlyList<MissingCardJson> NotCraftable,
    IReadOnlyList<MissingCardJson> UnknownCraftability,
    IReadOnlyList<UnknownDeckCard> UnknownCards,
    RotationJson Rotation,
    IReadOnlyList<string> Warnings)
{
    public static MissingReportJson From(MissingReport report, Core.Collection.CollectionSnapshot collection, Core.Cards.CardCatalog catalog) => new(
        report.DeckName,
        report.DeckClass,
        report.Deck.Format.ToString(),
        report.Deck.TotalCards,
        collection.BattleTag ?? collection.AccountKey,
        collection.TakenAt,
        collection.Source,
        catalog.Build,
        report.AsOf,
        report.DustAvailable,
        report.TotalCraftCost,
        report.Shortfall,
        report.Complete,
        report.MissingCraftable.Select(MissingCardJson.From).ToList(),
        report.MissingNotCraftable.Select(MissingCardJson.From).ToList(),
        report.MissingUnknownCraftability.Select(MissingCardJson.From).ToList(),
        report.UnknownCards,
        new RotationJson(report.Rotation.NextRotation, report.Rotation.DustAtRisk, report.Rotation.Cards.Select(MissingCardJson.From).ToList()),
        report.Warnings);
}

internal sealed record MissingCardJson(int DbfId, string CardId, string Name, string Rarity, string CardClass, string Set, int Needed, int Owned, int Missing, bool InSideboard, int? CraftCostEach, int? CraftCostMissing, bool RotatesNext)
{
    public static MissingCardJson From(DeckCardStatus s) => new(s.Card.DbfId, s.Card.CardId, s.Card.Name, s.Card.Rarity, s.Card.CardClass, s.Card.Set,
        s.Needed, s.Owned, s.Missing, s.InSideboard, s.CraftCostEach, s.CraftCostMissing, s.RotatesNext);
}

internal sealed record RotationJson(string NextRotation, int DustAtRisk, IReadOnlyList<MissingCardJson> Cards);
