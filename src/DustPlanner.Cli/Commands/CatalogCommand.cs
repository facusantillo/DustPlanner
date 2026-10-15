using System.CommandLine;
using System.Globalization;

namespace DustPlanner.Cli.Commands;

internal static class CatalogCommand
{
    public static Command Build(Func<ParseResult, CliContext> context)
    {
        var localeOpt = new Option<string>("--locale") { Description = "Locale to download (enUS, esES, esMX, ...).", DefaultValueFactory = _ => "enUS" };

        var update = new Command("update", "Download the latest card catalog from HearthstoneJSON (cached per client build).") { localeOpt };
        update.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            var loaded = await ctx.LoadCatalogAsync(forceRefresh: true, result.GetValue(localeOpt) ?? "enUS", ct).ConfigureAwait(false);
            var unknownSets = loaded.Catalog.Sets.Where(s => !ctx.Rules.Craftability.IsSetKnown(s)).Order(StringComparer.Ordinal).ToList();
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new { build = loaded.Catalog.Build, locale = loaded.Catalog.Locale, cards = loaded.Catalog.Count, from_cache = loaded.FromCache, unknown_sets = unknownSets, warning = loaded.Warning }).ConfigureAwait(false);
            }
            else
            {
                await ctx.Out.WriteLineAsync($"Catalog build {loaded.Catalog.Build} ({loaded.Catalog.Locale}): {loaded.Catalog.Count.ToString("N0", CultureInfo.InvariantCulture)} collectible cards{(loaded.FromCache ? " (from cache)" : string.Empty)}.").ConfigureAwait(false);
                if (unknownSets.Count > 0)
                {
                    await ctx.Out.WriteLineAsync("Sets not known to data/non-craftable.json (treated as unknown craftability): " + string.Join(", ", unknownSets)).ConfigureAwait(false);
                }
            }

            return 0;
        }));

        var info = new Command("info", "Show the cached catalog and the rule tables in use.");
        info.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            var loaded = await ctx.LoadCatalogAsync(forceRefresh: false, "enUS", ct).ConfigureAwait(false);
            var year = ctx.Rules.Rotation.YearOn(CliContext.Today);
            var rotating = ctx.Rules.Rotation.RotatingSets(CliContext.Today).Order(StringComparer.Ordinal).ToList();
            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    build = loaded.Catalog.Build,
                    cards = loaded.Catalog.Count,
                    cached_at = loaded.CachedAt,
                    year = year.Name,
                    standard_sets = year.Sets,
                    rotating_next = rotating,
                    next_rotation = ctx.Rules.Rotation.Next.Display,
                    premium_slots_confirmed = ctx.Rules.PremiumSlots.Confirmed,
                    home = ctx.Paths.Root,
                }).ConfigureAwait(false);
            }
            else
            {
                await ctx.Out.WriteLineAsync($"Home:            {ctx.Paths.Root}").ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Catalog:         build {loaded.Catalog.Build}, {loaded.Catalog.Count.ToString("N0", CultureInfo.InvariantCulture)} cards, checked {loaded.CachedAt:yyyy-MM-dd HH:mm}").ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Standard year:   {year.Name} (since {year.Starts:yyyy-MM-dd}): {string.Join(", ", year.Sets)}").ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Rotating next:   {string.Join(", ", rotating)} — {ctx.Rules.Rotation.Next.Display}").ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Premium slots:   {(ctx.Rules.PremiumSlots.Confirmed ? "confirmed" : "indexes 2/3 unconfirmed → copies there are excluded from recommendations")}").ConfigureAwait(false);
            }

            return 0;
        }));

        return new Command("catalog", "Card catalog and rule tables.") { update, info };
    }
}
