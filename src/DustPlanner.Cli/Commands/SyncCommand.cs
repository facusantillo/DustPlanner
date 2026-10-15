using System.CommandLine;
using DustPlanner.App;
using DustPlanner.App.Reports;
using System.Globalization;
using DustPlanner.Cli.Rendering;

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
            var summaries = ctx.App.SyncInbox(catalog, result.GetValue(accountOpt), result.GetValue(collectionOpt), result.GetValue(refundsOpt), CliContext.Today, DateTimeOffset.UtcNow);

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
}
