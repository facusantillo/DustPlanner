using System.Text.Json;
using DustPlanner.Core.Json;
using DustPlanner.Core.Planner;

namespace DustPlanner.Kick.Tests;

public class ChatPrefsTests
{
    [Fact]
    public void Defaults_match_the_prd()
    {
        var prefs = new ChatPrefs();
        Assert.Empty(prefs.Validate());
        Assert.Equal("!", prefs.Prefix);
        Assert.Null(prefs.Language);
        Assert.Equal(10, prefs.CooldownSeconds);
        Assert.Equal(30, prefs.UserCooldownSeconds);
        Assert.Equal(new[] { "deck", "rank", "wr", "last", "dust" }, prefs.Commands.Keys.OrderBy(k => Array.IndexOf(new[] { "deck", "rank", "wr", "last", "dust" }, k)));
        Assert.True(prefs.Commands["deck"].Enabled);
        Assert.False(prefs.Commands["dust"].Enabled);
        Assert.Equal("mazo", Assert.Single(prefs.Commands["deck"].Aliases));
        Assert.Equal("ultima", Assert.Single(prefs.Commands["last"].Aliases));
        Assert.Null(prefs.Commands["wr"].Template);
    }

    [Fact]
    public void Json_shape_is_the_one_documented_in_the_prd()
    {
        var json = JsonSerializer.Serialize(new UserPrefs(), CoreJson.Options);
        using var doc = JsonDocument.Parse(json);
        var chat = doc.RootElement.GetProperty("chat");
        Assert.Equal("!", chat.GetProperty("prefix").GetString());
        Assert.Equal(JsonValueKind.Null, chat.GetProperty("language").ValueKind);
        Assert.Equal(10, chat.GetProperty("cooldown_seconds").GetInt32());
        Assert.Equal(30, chat.GetProperty("user_cooldown_seconds").GetInt32());
        var deck = chat.GetProperty("commands").GetProperty("deck");
        Assert.True(deck.GetProperty("enabled").GetBoolean());
        Assert.Equal("mazo", deck.GetProperty("aliases")[0].GetString());
        Assert.Equal(JsonValueKind.Null, deck.GetProperty("template").ValueKind);

        // A prefs.json written before this block existed still loads, with the defaults.
        var old = JsonSerializer.Deserialize<UserPrefs>("""{ "format": "standard", "classes_played": ["PRIEST"] }""", CoreJson.Options)!;
        Assert.NotNull(old.Chat);
        Assert.Empty(old.Chat.Validate());
        Assert.True(old.Chat.Commands["rank"].Enabled);
    }

    [Fact]
    public void Validation_normalises_and_fills_missing_commands()
    {
        var prefs = new ChatPrefs
        {
            Prefix = " ! ",
            Language = " ES ",
            Commands = new Dictionary<string, ChatCommandPrefs>
            {
                ["Deck"] = new() { Aliases = { " Mazo ", "code", "", "mazo" }, Template = "  {deck_name}  " },
            },
        };

        Assert.Empty(prefs.Validate());
        Assert.Equal("!", prefs.Prefix);
        Assert.Equal("es", prefs.Language);
        Assert.Equal(new[] { "mazo", "code" }, prefs.Commands["deck"].Aliases);
        Assert.Equal("{deck_name}", prefs.Commands["deck"].Template);
        Assert.Equal(5, prefs.Commands.Count);
        Assert.False(prefs.Commands["dust"].Enabled);
        Assert.Equal("deck", prefs.Resolve("MAZO"));
        Assert.Equal("deck", prefs.Resolve("code"));
        Assert.Equal("wr", prefs.Resolve("winrate"));
        Assert.Null(prefs.Resolve("!deck"));
    }

    [Fact]
    public void Validation_reports_every_problem_in_plain_words()
    {
        var prefs = new ChatPrefs
        {
            Prefix = "ab",
            Language = "fr",
            CooldownSeconds = -1,
            UserCooldownSeconds = 99999,
            Commands = new Dictionary<string, ChatCommandPrefs>
            {
                ["deck"] = new() { Aliases = { "rank", "two words", "!x" }, Template = new string('a', 501) },
                ["rank"] = new() { Aliases = { "mazo" }, Template = "line\nbreak" },
                ["last"] = new() { Aliases = { "mazo" } },
                ["bogus"] = new(),
            },
        };

        var errors = prefs.Validate();

        Assert.Contains(errors, e => e.StartsWith("chat.prefix", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("chat.language", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("chat.cooldown_seconds", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("chat.user_cooldown_seconds", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'rank' is already a command", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'two words' must be one word", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'!x' must be one word", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("longer than 500", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("single line", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'mazo' is already", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("chat.commands.bogus", StringComparison.Ordinal));
        // "mazo" was kept by the first command that claimed it (rank, in iteration order of the known list after deck).
        Assert.DoesNotContain("mazo", prefs.Commands["last"].Aliases);
    }
}
