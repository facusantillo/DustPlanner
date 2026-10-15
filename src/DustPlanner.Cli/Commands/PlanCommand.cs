using System.CommandLine;
using DustPlanner.Cli.Rendering;
using DustPlanner.Contracts;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;
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
            var collectionFile = result.GetValue(collectionOpt);
            var account = result.GetValue(accountOpt);
            var collection = ctx.LoadCollection(collectionFile, account, result.GetValue(dustOpt), catalog);
            var asOf = result.GetValue(asOfOpt) ?? CliContext.Today;
            var missing = MissingCards.Analyze(deck, collection, catalog, ctx.Rules, asOf);

            var prefs = UserPrefs.LoadOrDefault(result.GetValue(prefsOpt) ?? Path.Combine(ctx.Paths.Config, UserPrefs.FileName));
            var meta = LoadMeta(result.GetValue(metaOpt) ?? Path.Combine(ctx.Paths.Config, "meta.txt"), catalog);
            var refunds = Refunds.LoadOrEmpty(result.GetValue(refundsOpt) ?? Path.Combine(ctx.Paths.Config, Refunds.FileName));
            var savedDecks = LoadSavedDecks(ctx, result.GetValue(decksOpt), collectionFile, collection.AccountKey, prefs);
            var manual = result.GetValue(protectOpt) ?? Array.Empty<string>();
            var protectedDecks = savedDecks.Concat(manual.Select((code, i) =>
            {
                var parsed = DeckBlockParser.Parse(code);
                return ProtectedDeck.FromDeckCode(parsed, parsed.Name ?? $"manual deck {i + 1}", ProtectedDecks.ManualOrigin, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            })).ToList();

            var plan = DisenchantPlanner.Plan(new PlanRequest(deck, missing, collection, catalog, ctx.Rules, prefs, protectedDecks, meta, refunds, asOf)
            {
                NeedOverride = result.GetValue(needOpt),
                AllowNoDecks = result.GetValue(allowNoDecksOpt),
            });

            var json = PlanJson.From(plan, missing, collection, catalog);
            long? savedId = null;
            if (plan.HasPlan && !result.GetValue(noSaveOpt))
            {
                using var store = DustStore.Open(ctx.Paths.Database);
                new SnapshotRepository(store).UpsertAccount(collection.AccountKey, collection.BattleTag, collection.Region, collection.TakenAt);
                savedId = new PlanRepository(store).Save(collection.AccountKey, deck, plan, json, DateTimeOffset.UtcNow);
            }

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(json with { SavedPlanId = savedId }).ConfigureAwait(false);
            }
            else
            {
                await ctx.Out.WriteAsync(PlanRenderer.Render(plan, missing, collection, catalog)).ConfigureAwait(false);
                if (savedId is { } id)
                {
                    await ctx.Out.WriteLineAsync($"Plan #{id} stored; 'dustplanner sync' after disenchanting will match it against your next export.").ConfigureAwait(false);
                }
            }

            return plan.HasPlan || plan.Need <= 0 ? 0 : 1;
        }));

        return command;
    }

    private static MetaSignal LoadMeta(string path, Core.Cards.CardCatalog catalog) =>
        File.Exists(path) ? MetaSignal.FromText(File.ReadAllText(path), catalog) : MetaSignal.Empty;

    private static IReadOnlyList<ProtectedDeck> LoadSavedDecks(CliContext ctx, string? decksFile, string? collectionFile, string accountKey, UserPrefs prefs)
    {
        if (decksFile is not null)
        {
            if (!File.Exists(decksFile))
            {
                throw new CliException($"Decks file not found: {decksFile}");
            }

            return ProtectedDecks.FromExportFile(decksFile, prefs);
        }

        if (collectionFile is not null)
        {
            // Explicit collection file: look for a decks.json next to it, otherwise none.
            var sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(collectionFile)) ?? ".", ExportSchema.DecksFileName);
            return File.Exists(sibling) ? ProtectedDecks.FromExportFile(sibling, prefs) : Array.Empty<ProtectedDeck>();
        }

        var inbox = Path.Combine(ctx.Paths.Inbox, accountKey, ExportSchema.DecksFileName);
        if (File.Exists(inbox))
        {
            return ProtectedDecks.FromExportFile(inbox, prefs);
        }

        // Fallback: decks previously imported into the database by 'sync'.
        if (File.Exists(ctx.Paths.Database))
        {
            using var store = DustStore.Open(ctx.Paths.Database, readOnly: true);
            return new DeckRepository(store).ProtectedDecks(accountKey, prefs);
        }

        return Array.Empty<ProtectedDeck>();
    }
}

internal sealed record PlanJson(
    MissingReportJson Missing,
    int Need,
    bool HasPlan,
    PlanResultJson? Main,
    IReadOnlyList<PlanAlternativeJson> Alternatives,
    IReadOnlyList<ExclusionJson> NotableExclusions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    int ProtectedDeckCount,
    int CandidateDustTotal)
{
    public long? SavedPlanId { get; init; }

    public static PlanJson From(DisenchantPlan plan, MissingReport missing, Core.Collection.CollectionSnapshot collection, Core.Cards.CardCatalog catalog) => new(
        MissingReportJson.From(missing, collection, catalog),
        plan.Need,
        plan.HasPlan,
        plan.Main is { Feasible: true } m ? PlanResultJson.From(m) : null,
        plan.Alternatives.Select(a => new PlanAlternativeJson(a.Label, PlanResultJson.From(a.Result))).ToList(),
        plan.NotableExclusions.Select(e => new ExclusionJson(e.DbfId, e.Card?.Name, CandidateBuilder.KindName(e.Kind), e.Copies, e.DustEach, e.Reason)).ToList(),
        plan.Warnings,
        plan.Blockers,
        plan.ProtectedDeckCount,
        plan.Pool.TotalDust);
}

internal sealed record PlanResultJson(int TotalDust, int Surplus, int TotalPain, int TotalCopies, IReadOnlyList<PickJson> Picks)
{
    public static PlanResultJson From(OptimizerResult r) => new(r.TotalDust, r.Surplus, r.TotalPain, r.TotalCopies,
        r.Picks.Select(p => new PickJson(p.Group.Card.DbfId, p.Group.Card.CardId, p.Group.Card.Name, p.Group.Card.Rarity, p.Group.Card.CardClass, p.Group.Card.Set,
            CandidateBuilder.KindName(p.Group.Kind), p.Copies, p.Group.DustEach, p.Dust, p.Group.PainEach, p.Group.Category.ToString(), p.Group.Reasons, p.Group.RefundActive)).ToList());
}

internal sealed record PlanAlternativeJson(string Label, PlanResultJson Result);

internal sealed record PickJson(int DbfId, string CardId, string Name, string Rarity, string CardClass, string Set, string Kind, int Copies, int DustEach, int DustTotal, int PainEach, string Category, IReadOnlyList<string> Reasons, bool RefundActive);

internal sealed record ExclusionJson(int DbfId, string? Name, string Kind, int Copies, int? DustEach, string Reason);
