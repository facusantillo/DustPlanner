using System.CommandLine;
using DustPlanner.Cli;
using DustPlanner.Cli.Commands;
using DustPlanner.Core;

var jsonOption = new Option<bool>("--json") { Description = "Machine-readable output (snake_case JSON).", Recursive = true };
var homeOption = new Option<string?>("--home") { Description = $"DustPlanner data folder (default: %LocalAppData%\\DustPlanner or ${AppPaths.HomeVariable}).", Recursive = true };

var root = new RootCommand("DustPlanner — read-only Hearthstone dust planner. Never touches the game client.");
root.Options.Add(jsonOption);
root.Options.Add(homeOption);

CliContext Context(ParseResult result)
{
    var home = result.GetValue(homeOption);
    var paths = home is null ? AppPaths.Default() : new AppPaths(Path.GetFullPath(home));
    return new CliContext(paths, Console.Out, Console.Error, result.GetValue(jsonOption));
}

root.Subcommands.Add(NeedCommand.Build(Context));
root.Subcommands.Add(PlanCommand.Build(Context));
root.Subcommands.Add(CatalogCommand.Build(Context));
root.Subcommands.Add(CollectionCommand.Build(Context));
root.Subcommands.Add(SyncCommand.Build(Context));
root.Subcommands.Add(HistoryCommand.Build(Context));
root.Subcommands.Add(GamesCommand.Build(Context));

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
