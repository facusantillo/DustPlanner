using DustPlanner.Core.Cards;
using DustPlanner.LogTracker;
using DustPlanner.Store;

namespace DustPlanner.LogTracker.Tests;

public class TrackerIntegrationTests
{
    [Fact]
    public void Importer_stores_games_once_and_stats_count_them()
    {
        var logs = Path.Combine(Path.GetTempPath(), "dustplanner-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session1 = Path.Combine(logs, "Hearthstone_2026_10_15_23_50_00");
            var session2 = Path.Combine(logs, "Hearthstone_2026_10_16_10_00_00");
            Directory.CreateDirectory(session1);
            Directory.CreateDirectory(session2);
            // Session 1 as HDT leaves it: it renamed Power.log to Power_old.log when it (re)started mid-session and the
            // game wrote the rest to a fresh Power.log. The first game crosses midnight inside the rotated file.
            var lines = File.ReadAllLines(PowerLogParserTests.Fixture("synthetic_edge_cases.power.log"));
            var firstGameEnd = Array.FindIndex(lines, l => l.Contains("tag=STATE value=COMPLETE", StringComparison.Ordinal)) + 1;
            File.WriteAllLines(Path.Combine(session1, "Power_old.log"), lines.Take(firstGameEnd));
            File.WriteAllLines(Path.Combine(session1, "Power.log"), lines.Skip(firstGameEnd));
            File.Copy(PowerLogParserTests.Fixture("ranked_standard_both_names.power.log"), Path.Combine(session2, "Power.log"));

            var found = LogLocator.FindPowerLogs(logs);
            Assert.Equal(3, found.Count);
            Assert.Equal(new DateOnly(2026, 10, 15), found[0].BaseDate);
            Assert.True(found[0].Rotated);
            Assert.Equal(session1, found[1].Session);
            Assert.False(found[1].Rotated);
            Assert.Equal(new DateOnly(2026, 10, 16), found[2].BaseDate);

            using var store = DustStore.OpenInMemory();
            var games = new GameRepository(store);
            var catalog = HearthstoneJsonParser.Parse(File.ReadAllText(PowerLogParserTests.Fixture("cards.synthetic.json")), "synthetic", "enUS");
            var importer = new LogImporter(games, catalog, TimeZoneInfo.Utc);

            var first = importer.ImportDirectory(logs);
            Assert.Equal(3, first.FilesRead);
            Assert.Equal(4, first.GamesParsed);   // 3 in the synthetic log (one unfinished) + 1 real
            Assert.Equal(4, first.GamesInserted);
            Assert.Equal(1, first.Incomplete);

            var again = importer.ImportDirectory(logs);
            Assert.Equal(0, again.GamesInserted);

            var all = games.List(null, 10);
            Assert.Equal(4, all.Count);
            Assert.Equal("8589934592-42", all.First(g => g.Result == "loss").AccountKey);

            var mine = games.Stats("8589934592-42", since: null);
            Assert.Equal(3, mine.Games);
            Assert.Equal(0, mine.Wins);
            Assert.Equal(1, mine.Losses);
            Assert.Equal(1, mine.Ties);
            Assert.Equal(1, mine.Unknown);
            Assert.Equal(0.0, mine.Percentage);

            var ranked = games.Stats("8589934592-42", since: null, gameType: "GT_RANKED");
            Assert.Equal(2, ranked.Games);

            var sinceMidnight = games.Stats("8589934592-42", since: new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
            Assert.Equal(2, sinceMidnight.Games); // the concede started on the 15th

            var byClass = games.StatsByClass("8589934592-42", null);
            Assert.Contains(byClass, c => c.PlayerClass == "PRIEST" && c.Rate.Games == 2);
            var standardOnly = games.StatsByClass("8589934592-42", null, format: "FT_STANDARD");
            Assert.Equal(1, standardOnly.Single(c => c.PlayerClass == "PRIEST").Rate.Games); // the AI tie is Wild

            Assert.Equal(["144115193835963207-66765537", "8589934592-42"], games.Accounts());
        }
        finally
        {
            TempDir.Delete(logs);
        }
    }

    [Fact]
    public void Follower_reports_games_appended_after_it_started()
    {
        var logs = Path.Combine(Path.GetTempPath(), "dustplanner-follow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = Path.Combine(logs, "Hearthstone_2026_10_15_23_50_00");
            Directory.CreateDirectory(session);
            var path = Path.Combine(session, "Power.log");
            var lines = File.ReadAllLines(PowerLogParserTests.Fixture("synthetic_edge_cases.power.log"));
            var firstGameEnd = Array.FindIndex(lines, l => l.Contains("tag=STATE value=COMPLETE", StringComparison.Ordinal)) + 1;
            File.WriteAllLines(path, lines.Take(firstGameEnd));

            var follower = new PowerLogFollower(logs, TimeZoneInfo.Utc);
            var completed = new List<GameRecord>();
            follower.GameCompleted += completed.Add;

            Assert.Equal(0, follower.Poll()); // existing content skipped
            Assert.Empty(completed);

            using (var writer = new StreamWriter(path, append: true))
            {
                foreach (var line in lines.Skip(firstGameEnd))
                {
                    writer.WriteLine(line);
                }
            }

            Assert.True(follower.Poll() > 0);
            var game = Assert.Single(completed); // the tie; the third game is still open
            Assert.Equal("tie", game.Result);
            Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 5, 0, TimeSpan.Zero), game.StartedAt); // the replayed first game crossed midnight
            Assert.NotNull(follower.CurrentGame);
            Assert.Equal("GT_RANKED", follower.CurrentGame!.GameType);

            // A new session folder (game relaunched) becomes the followed file.
            var session2 = Path.Combine(logs, "Hearthstone_2026_10_16_10_00_00");
            Directory.CreateDirectory(session2);
            File.Copy(PowerLogParserTests.Fixture("ranked_standard_both_names.power.log"), Path.Combine(session2, "Power.log"));
            Assert.True(follower.Poll() > 0);
            Assert.Equal(2, completed.Count);
            Assert.EndsWith("Hearthstone_2026_10_16_10_00_00" + Path.DirectorySeparatorChar + "Power.log", follower.CurrentPath, StringComparison.Ordinal);
        }
        finally
        {
            TempDir.Delete(logs);
        }
    }

    [Fact]
    public void Follower_survives_hdt_renaming_the_file_it_tails()
    {
        var logs = Path.Combine(Path.GetTempPath(), "dustplanner-rotate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = Path.Combine(logs, "Hearthstone_2026_10_15_23_50_00");
            Directory.CreateDirectory(session);
            var live = Path.Combine(session, "Power.log");
            var rotated = Path.Combine(session, "Power_old.log");
            var lines = File.ReadAllLines(PowerLogParserTests.Fixture("synthetic_edge_cases.power.log"));
            var ends = lines.Select((l, i) => (l, i)).Where(x => x.l.Contains("tag=STATE value=COMPLETE", StringComparison.Ordinal)).Select(x => x.i + 1).ToList();
            File.WriteAllLines(live, lines.Take(ends[0]));

            var follower = new PowerLogFollower(logs, TimeZoneInfo.Utc);
            var completed = new List<GameRecord>();
            follower.GameCompleted += completed.Add;
            Assert.Equal(0, follower.Poll());

            // HDT restarts: Power.log becomes Power_old.log and the game keeps appending to the renamed file.
            File.Move(live, rotated);
            File.AppendAllLines(rotated, lines.Skip(ends[0]).Take(ends[1] - ends[0]));
            Assert.True(follower.Poll() > 0);
            var tie = Assert.Single(completed);
            Assert.Equal("tie", tie.Result);
            Assert.Equal(new DateOnly(2026, 10, 16), DateOnly.FromDateTime(tie.StartedAt.UtcDateTime)); // the clock survived the rename
            Assert.EndsWith("Power_old.log", follower.CurrentPath, StringComparison.Ordinal);

            // The game opens a fresh Power.log in the same session: same parser, the new game is seen from its first line.
            File.WriteAllLines(live, lines.Skip(ends[1]));
            follower.Poll();
            Assert.Single(completed);
            Assert.EndsWith(Path.DirectorySeparatorChar + "Power.log", follower.CurrentPath, StringComparison.Ordinal);
            Assert.NotNull(follower.CurrentGame);
            Assert.Equal("GT_RANKED", follower.CurrentGame!.GameType);
            Assert.Equal(new DateOnly(2026, 10, 16), DateOnly.FromDateTime(follower.CurrentGame.StartedAt.UtcDateTime));
        }
        finally
        {
            TempDir.Delete(logs);
        }
    }

    [Fact]
    public void Log_config_adds_or_completes_the_power_section_without_touching_others()
    {
        const string hdtStyle = "[Achievements]\nLogLevel=1\nFilePrinting=true\nConsolePrinting=false\nScreenPrinting=false\nVerbose=false\n\n[Power]\nLogLevel=1\nFilePrinting=false\nConsolePrinting=false\nScreenPrinting=false\n";

        Assert.False(LogConfig.IsConfigured(hdtStyle));
        var fixed1 = LogConfig.Apply(hdtStyle);
        Assert.True(LogConfig.IsConfigured(fixed1));
        Assert.Contains("[Achievements]", fixed1, StringComparison.Ordinal);
        Assert.Contains("FilePrinting=true", fixed1[fixed1.IndexOf("[Power]", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("Verbose=true", fixed1, StringComparison.Ordinal);
        Assert.Equal(fixed1, LogConfig.Apply(fixed1));

        var fromScratch = LogConfig.Apply(string.Empty);
        Assert.True(LogConfig.IsConfigured(fromScratch));
        Assert.StartsWith("[Power]", fromScratch, StringComparison.Ordinal);

        var file = Path.Combine(Path.GetTempPath(), "dustplanner-logconfig-" + Guid.NewGuid().ToString("N"), "log.config");
        try
        {
            Assert.True(LogConfig.EnsureFile(file));
            Assert.False(LogConfig.EnsureFile(file));
            Assert.True(LogConfig.IsConfigured(File.ReadAllText(file)));
        }
        finally
        {
            TempDir.Delete(Path.GetDirectoryName(file)!);
        }
    }
}
