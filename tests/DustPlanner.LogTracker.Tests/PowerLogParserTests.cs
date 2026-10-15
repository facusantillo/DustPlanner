using DustPlanner.LogTracker;

namespace DustPlanner.LogTracker.Tests;

public class PowerLogParserTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static PowerLogParser ParseFile(string name, DateOnly baseDate)
    {
        var parser = new PowerLogParser(baseDate);
        parser.Feed(File.ReadLines(Fixture(name)));
        return parser;
    }

    [Fact]
    public void Ranked_standard_game_with_both_names_is_fully_reconstructed()
    {
        var parser = ParseFile("ranked_standard_both_names.power.log", new DateOnly(2024, 5, 1));

        var game = Assert.Single(parser.Games);
        Assert.True(game.Completed);
        Assert.Equal("GT_RANKED", game.GameType);
        Assert.Equal("FT_STANDARD", game.FormatType);
        Assert.Equal(88998, game.BuildNumber);
        Assert.Equal(32, game.Turns);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 20, 24, 43, TimeSpan.Zero), game.StartedAt);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 20, 26, 56, TimeSpan.Zero), game.EndedAt);

        var p1 = game.Players.Single(p => p.PlayerId == 1);
        var p2 = game.Players.Single(p => p.PlayerId == 2);
        Assert.Equal("Maievs#11864", p1.Name);
        Assert.Equal("Frost#11839", p2.Name);
        Assert.Equal("HERO_01", p1.HeroCardId);
        Assert.Equal("HERO_04b", p2.HeroCardId);
        Assert.Equal("WON", p1.PlayState); // written as "Entity=Maievs" without the discriminator
        Assert.Equal("LOST", p2.PlayState);
        Assert.False(p1.IsFirst);
        Assert.True(p2.IsFirst); // FIRST_PLAYER=1 is in the second player block of this (reconnect) log
        Assert.Equal(66765537UL, p1.AccountLo);
        Assert.NotNull(game.Friendly);
        Assert.Equal(game.Friendly!.PlayState == "WON" ? "win" : "loss", game.Result);
        Assert.Equal("WARRIOR", HeroClasses.ClassOf(p1.HeroCardId, null));
        Assert.Equal("PALADIN", HeroClasses.ClassOf(p2.HeroCardId, null));
        Assert.Contains("88998", game.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_human_player_name_is_learned_from_later_lines()
    {
        var parser = ParseFile("ranked_wild_unknown_name.power.log", new DateOnly(2024, 1, 1));

        var game = Assert.Single(parser.Games);
        Assert.True(game.Completed);
        Assert.Equal("FT_WILD", game.FormatType);
        var p1 = game.Players.Single(p => p.PlayerId == 1);
        var p2 = game.Players.Single(p => p.PlayerId == 2);
        Assert.Equal("Kaibu#11431", p1.Name);
        Assert.Equal("Eyeronic#11298", p2.Name);
        Assert.Equal("WON", p1.PlayState);
        Assert.Equal("LOST", p2.PlayState);
        Assert.Equal("WARLOCK", HeroClasses.ClassOf(p1.HeroCardId, null));
        Assert.Equal("DRUID", HeroClasses.ClassOf(p2.HeroCardId, null));
        Assert.Equal(10, game.Turns);
        Assert.NotNull(game.Friendly);
        Assert.NotEqual(game.Friendly!.PlayerId, game.Opponent!.PlayerId);
    }

    [Fact]
    public void Friendly_player_is_the_one_whose_hand_is_revealed()
    {
        var parser = ParseFile("casual_inferrable_player.power.log", new DateOnly(2019, 3, 1));

        var game = Assert.Single(parser.Games);
        Assert.Equal("GT_CASUAL", game.GameType);
        Assert.Equal(25770, game.BuildNumber);
        var friendly = game.Friendly!;
        Assert.Equal("shukrutav#1208", friendly.Name);
        Assert.Equal("HERO_05", friendly.HeroCardId);
        Assert.Equal("win", game.Result);
        Assert.Equal("jspoon#1665", game.Opponent!.Name);
        Assert.Equal("HERO_06", game.Opponent.HeroCardId);
        Assert.Equal(25, game.Turns);
    }

    [Fact]
    public void Synthetic_log_covers_concede_midnight_rollover_ai_opponent_tie_and_unfinished_game()
    {
        var parser = ParseFile("synthetic_edge_cases.power.log", new DateOnly(2026, 10, 15));
        var open = parser.Flush();

        Assert.Equal(2, parser.Games.Count);

        var first = parser.Games[0];
        Assert.True(first.Completed);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 23, 58, 10, TimeSpan.Zero), first.StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 1, 0, TimeSpan.Zero), first.EndedAt); // rolled over midnight
        Assert.Equal("Synthetic#0001", first.Friendly!.Name);
        Assert.True(first.Friendly.IsFriendly);
        Assert.Equal("loss", first.Result); // CONCEDED then LOST
        Assert.Equal("Rival#2222", first.Opponent!.Name); // learned from the MULLIGAN_STATE line
        Assert.Equal("HERO_09", first.Friendly.HeroCardId); // hero power HERO_POWER card type ignored
        Assert.Equal("HERO_08a", first.Opponent.HeroCardId);
        Assert.False(first.Friendly.IsFirst);
        Assert.True(first.Opponent.IsFirst);
        Assert.Equal(3, first.Turns);
        Assert.Equal("8589934592-42", first.Friendly.AccountKey);

        var second = parser.Games[1];
        Assert.Equal("GT_VS_AI", second.GameType);
        Assert.Equal("tie", second.Result);
        Assert.Equal("Synthetic#0001", second.Friendly!.Name); // AI opponent (lo=0) → the human is friendly
        Assert.True(second.Opponent!.IsAi);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 5, 0, TimeSpan.Zero), second.StartedAt);

        Assert.NotNull(open);
        Assert.False(open!.Completed);
        Assert.Equal("unknown", open.Result);
        Assert.Null(parser.Current);
    }

    [Fact]
    public void Lines_from_other_loggers_and_garbage_are_ignored()
    {
        var parser = new PowerLogParser(new DateOnly(2026, 1, 1));
        parser.Feed("D 10:00:00.0000000 PowerTaskList.DebugPrintPower() - CREATE_GAME");
        parser.Feed("garbage");
        parser.Feed(string.Empty);
        parser.Feed("D 10:00:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE ");

        Assert.Empty(parser.Games);
        Assert.Null(parser.Current);
    }
}
