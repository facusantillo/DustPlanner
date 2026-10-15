using System.CommandLine;
using System.Globalization;
using DustPlanner.Cli.Rendering;
using DustPlanner.Core.Planner;
using DustPlanner.Store;

namespace DustPlanner.Cli.Commands;

internal static class HistoryCommand
{
    public static Command Build(Func<ParseResult, CliContext> context)
    {
        var accountOpt = new Option<string?>("--account", "-a") { Description = "Account key (default: the most recently seen account in the database)." };
        var limitOpt = new Option<int>("--limit") { Description = "Maximum events to show.", DefaultValueFactory = _ => 50 };

        var command = new Command("history", "Collection changes detected between snapshots, and the disenchant plans they were matched against.") { accountOpt, limitOpt };

        command.SetAction(CliAction.Guarded(async (result, ct) =>
        {
            var ctx = context(result);
            if (!File.Exists(ctx.Paths.Database))
            {
                throw new CliException($"No database yet at {ctx.Paths.Database}. Run 'dustplanner sync' first.");
            }

            var catalog = (await ctx.LoadCatalogAsync(forceRefresh: false, "enUS", ct).ConfigureAwait(false)).Catalog;
            using var store = DustStore.Open(ctx.Paths.Database, readOnly: true);
            var snapshots = new SnapshotRepository(store);
            var known = snapshots.Accounts();
            var accountKey = result.GetValue(accountOpt) ?? (known.Count > 0 ? known[0] : null)
                ?? throw new CliException("The database holds no account yet. Run 'dustplanner sync' first.");

            var events = new HistoryService(store).Events(accountKey, result.GetValue(limitOpt));
            var plans = new PlanRepository(store).List(accountKey);
            var snapshotList = snapshots.List(accountKey, 10);

            if (ctx.Json)
            {
                await ctx.WriteJsonAsync(new
                {
                    account_key = accountKey,
                    snapshots = snapshotList,
                    plans,
                    events = events.Select(e => new EventJson(e.Id, e.DbfId, catalog.GetByDbfId(e.DbfId)?.Name ?? $"dbfId {e.DbfId}", CandidateBuilder.KindName(e.Kind), e.Delta, e.EventKind, e.DustEstimate, e.PlanId, e.ConfidenceLevel, e.DetectedAt)),
                }).ConfigureAwait(false);
                return 0;
            }

            await ctx.Out.WriteLineAsync($"Account {accountKey} · {snapshotList.Count} snapshot(s) stored" + (snapshotList.Count > 0 ? $" · latest {snapshotList[0].TakenAt.ToLocalTime():yyyy-MM-dd HH:mm} ({snapshotList[0].Dust.ToString("N0", CultureInfo.InvariantCulture)} dust)" : string.Empty)).ConfigureAwait(false);

            await ctx.Out.WriteLineAsync().ConfigureAwait(false);
            await ctx.Out.WriteLineAsync("PLANS").ConfigureAwait(false);
            if (plans.Count == 0)
            {
                await ctx.Out.WriteLineAsync("  none yet ('dustplanner plan <deck>' stores one)").ConfigureAwait(false);
            }
            else
            {
                var table = new TextTable().AddColumn("#", true).AddColumn("created").AddColumn("target").AddColumn("need", true).AddColumn("gives", true).AddColumn("pain", true).AddColumn("status");
                foreach (var p in plans)
                {
                    table.AddRow(p.Id, p.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), p.TargetDeckName ?? "(unnamed)", p.Need, p.TotalDust, p.TotalPain, p.Status);
                }

                await ctx.Out.WriteAsync(table.Render()).ConfigureAwait(false);
            }

            await ctx.Out.WriteLineAsync().ConfigureAwait(false);
            await ctx.Out.WriteLineAsync("CHANGES (newest first)").ConfigureAwait(false);
            if (events.Count == 0)
            {
                await ctx.Out.WriteLineAsync("  none detected yet (changes appear after the second 'sync' with a different collection)").ConfigureAwait(false);
            }
            else
            {
                var table = new TextTable().AddColumn("detected").AddColumn("change").AddColumn("card").AddColumn("kind").AddColumn("copies", true).AddColumn("dust est.", true).AddColumn("plan").AddColumn("confidence");
                foreach (var e in events)
                {
                    table.AddRow(e.DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), e.EventKind, catalog.GetByDbfId(e.DbfId)?.Name ?? $"dbfId {e.DbfId}",
                        CandidateBuilder.KindName(e.Kind), Math.Abs(e.Delta), e.DustEstimate, e.PlanId is { } p ? "#" + p.ToString(CultureInfo.InvariantCulture) : "-", e.ConfidenceLevel);
                }

                await ctx.Out.WriteAsync(table.Render()).ConfigureAwait(false);
            }

            return 0;
        }));

        return command;
    }
}
