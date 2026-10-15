using DustPlanner.Core.Cards;

namespace DustPlanner.LogTracker;

public sealed record ImportSummary(int FilesRead, int GamesParsed, int GamesInserted, int Incomplete, IReadOnlyList<string> Warnings);

/// <summary>Parses every Power.log in a logs folder and stores the games it finds. Re-running is safe (games are keyed).</summary>
public sealed class LogImporter
{
    private readonly GameRepository _games;
    private readonly CardCatalog? _catalog;

    public LogImporter(GameRepository games, CardCatalog? catalog)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _catalog = catalog;
    }

    public ImportSummary ImportDirectory(string logsDirectory)
    {
        var files = LogLocator.FindPowerLogs(logsDirectory);
        var warnings = new List<string>();
        int parsed = 0, inserted = 0, incomplete = 0;
        foreach (var file in files)
        {
            var (p, i, inc) = ImportFile(file, warnings);
            parsed += p;
            inserted += i;
            incomplete += inc;
        }

        return new ImportSummary(files.Count, parsed, inserted, incomplete, warnings);
    }

    public (int Parsed, int Inserted, int Incomplete) ImportFile(PowerLogFile file, List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var parser = new PowerLogParser(file.BaseDate);
        using (var reader = new StreamReader(new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            while (reader.ReadLine() is { } line)
            {
                parser.Feed(line);
            }
        }

        var open = parser.Flush();
        var records = parser.Games.ToList();
        if (open is not null)
        {
            records.Add(open);
        }

        int inserted = 0, incomplete = 0;
        foreach (var game in records)
        {
            if (game.Friendly is null)
            {
                warnings?.Add($"{Path.GetFileName(Path.GetDirectoryName(file.Path))}: game at {game.StartedAt:HH:mm:ss} has no identifiable local player; skipped.");
                continue;
            }

            if (!game.Completed)
            {
                incomplete++;
            }

            if (_games.Insert(game, _catalog))
            {
                inserted++;
            }
        }

        return (records.Count, inserted, incomplete);
    }
}
