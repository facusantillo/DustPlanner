using System.Globalization;
using System.Text;
using DustPlanner.Core.Planner;
using DustPlanner.Kick;

namespace DustPlanner.Kick.Tests;

public class ChatResponderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 20, 0, 0, TimeSpan.Zero);

    private static ChatPrefs Prefs(string? language = "es")
    {
        var prefs = new ChatPrefs { Language = language };
        Assert.Empty(prefs.Validate());
        return prefs;
    }

    private static ChatContext Full() => new()
    {
        Deck = new ChatDeck("Priest Control", "AAECAa0GAA==", "PRIEST", "standard"),
        Rank = new ChatRank(5, 8, 2, 0, "standard"),
        Today = new ChatRecord(7, 3, 0),
        Session = new ChatRecord(4, 1, 0),
        Streak = 3,
        LastGame = new ChatLastGame("win", "PRIEST", "MAGE", 9),
        Dust = 1240,
    };

    [Theory]
    [InlineData("deck", "es", "Priest Control · AAECAa0GAA==")]
    [InlineData("mazo", "es", "Priest Control · AAECAa0GAA==")]
    [InlineData("deck", "en", "Priest Control · AAECAa0GAA==")]
    [InlineData("rank", "es", "Diamante 3 · Estándar")]
    [InlineData("rango", "es", "Diamante 3 · Estándar")]
    [InlineData("rank", "en", "Diamond 3 · Standard")]
    [InlineData("wr", "es", "Hoy 7-3 (70 %) · sesión 4-1 · racha de 3 victorias")]
    [InlineData("winrate", "en", "Today 7-3 (70%) · session 4-1 · 3-win streak")]
    [InlineData("last", "es", "Última: victoria contra Mago en 9 turnos")]
    [InlineData("ultima", "en", "Last: win vs Mage in 9 turns")]
    public void Full_state_renders_the_documented_examples(string word, string language, string expected)
    {
        var reply = new ChatResponder().Respond(Prefs(language), word, "viewer", null, Full(), T0);

        Assert.Equal(ChatReplyStatus.Ok, reply.Status);
        Assert.Equal(expected, reply.Text);
    }

    [Theory]
    [InlineData("deck", "es", "Todavía no hay mazo activo")]
    [InlineData("rank", "es", "Todavía no hay rango exportado")]
    [InlineData("wr", "es", "Todavía no hay partidas hoy")]
    [InlineData("last", "es", "Todavía no terminó ninguna partida")]
    [InlineData("deck", "en", "No active deck yet")]
    [InlineData("wr", "en", "No games today yet")]
    public void Empty_state_answers_with_a_friendly_line_not_an_error(string word, string language, string expected)
    {
        var reply = new ChatResponder().Respond(Prefs(language), word, null, null, ChatContext.Empty, T0);

        Assert.Equal(ChatReplyStatus.Ok, reply.Status);
        Assert.Equal(expected, reply.Text);
    }

    [Fact]
    public void Partial_state_fills_what_it_has()
    {
        var context = new ChatContext { Today = new ChatRecord(0, 0, 1), Session = new ChatRecord(0, 0, 1), Rank = new ChatRank(0, 0, 0, 1234, "standard") };
        var responder = new ChatResponder();

        // One draw: games exist, nothing decided → the percentage is a dash, not a crash or a division by zero.
        Assert.Equal("Hoy 0-0 (—) · sesión 0-0", responder.Respond(Prefs(), "wr", null, null, context, T0).Text);
        Assert.Equal("Leyenda #1.234 · Estándar", responder.Respond(Prefs(), "rank", null, null, context, T0).Text);
        Assert.Equal("Legend #1,234 · Standard", responder.Respond(Prefs("en"), "rank", null, null, context, T0.AddMinutes(1)).Text);
    }

    [Fact]
    public void Dust_is_off_by_default_and_unknown_words_are_unknown()
    {
        var responder = new ChatResponder();
        Assert.Equal(ChatReplyStatus.Disabled, responder.Respond(Prefs(), "dust", null, null, Full(), T0).Status);
        Assert.Equal(ChatReplyStatus.Disabled, responder.Respond(Prefs(), "polvo", null, null, Full(), T0).Status);
        Assert.Equal(ChatReplyStatus.UnknownCommand, responder.Respond(Prefs(), "giveaway", null, null, Full(), T0).Status);
        Assert.Equal(ChatReplyStatus.UnknownCommand, responder.Respond(Prefs(), "", null, null, Full(), T0).Status);

        var prefs = Prefs();
        prefs.Commands["dust"].Enabled = true;
        Assert.Equal("1.240 de polvo", responder.Respond(prefs, "dust", null, null, Full(), T0).Text);
        Assert.Equal("1,240 dust", responder.Respond(prefs, "dust", null, "en", Full(), T0.AddMinutes(1)).Text);
    }

    [Fact]
    public void Cooldowns_apply_per_command_globally_and_per_viewer()
    {
        var prefs = Prefs();
        var responder = new ChatResponder();

        Assert.Equal(ChatReplyStatus.Ok, responder.Respond(prefs, "deck", "ana", null, Full(), T0).Status);
        // 5 s later anyone asking the same command is cooled down (global 10 s); another command is not.
        Assert.Equal(ChatReplyStatus.Cooldown, responder.Respond(prefs, "deck", "bob", null, Full(), T0.AddSeconds(5)).Status);
        Assert.Equal(ChatReplyStatus.Ok, responder.Respond(prefs, "rank", "bob", null, Full(), T0.AddSeconds(5)).Status);
        // 15 s later the global window passed: bob gets his answer, but ana is still within her own 30 s.
        Assert.Equal(ChatReplyStatus.Ok, responder.Respond(prefs, "deck", "bob", null, Full(), T0.AddSeconds(15)).Status);
        Assert.Equal(ChatReplyStatus.Cooldown, responder.Respond(prefs, "deck", "Ana", null, Full(), T0.AddSeconds(26)).Status);
        Assert.Equal(ChatReplyStatus.Ok, responder.Respond(prefs, "deck", "ana", null, Full(), T0.AddSeconds(31)).Status);
        // Without a viewer only the global cooldown applies.
        Assert.Equal(ChatReplyStatus.Cooldown, responder.Respond(prefs, "deck", null, null, Full(), T0.AddSeconds(35)).Status);
        Assert.Equal(ChatReplyStatus.Ok, responder.Respond(prefs, "deck", null, null, Full(), T0.AddSeconds(41)).Status);

        // Cooled-down and unknown requests never count as an answer.
        responder.ResetCooldowns();
        Assert.Equal(ChatReplyStatus.UnknownCommand, responder.Respond(prefs, "nope", "ana", null, Full(), T0).Status);
        Assert.Equal(ChatReplyStatus.Ok, responder.Respond(prefs, "deck", "ana", null, Full(), T0).Status);
    }

    [Fact]
    public void Custom_templates_use_placeholders_and_never_start_with_the_prefix()
    {
        var prefs = Prefs();
        prefs.Commands["deck"].Template = "!deck de {user}: {deck_name} ({deck_class}, {deck_format}) {unknown_marker}";
        var reply = new ChatResponder().Respond(prefs, "deck", "Ana", null, Full(), T0);

        Assert.Equal("· !deck de Ana: Priest Control (Sacerdote, Estándar) {unknown_marker}", reply.Text);
    }

    [Fact]
    public void Deck_link_appears_only_when_the_public_page_is_on()
    {
        var context = Full() with { DeckUrl = "https://live.dustplanner.app/d/tsukuru_tv" };
        var reply = new ChatResponder().Respond(Prefs(), "deck", null, null, context, T0);
        Assert.Equal("Priest Control · lista: https://live.dustplanner.app/d/tsukuru_tv · AAECAa0GAA==", reply.Text);
    }

    [Fact]
    public void Answers_fit_the_platform_limits_in_graphemes_and_bytes()
    {
        var longName = new string('x', 600);
        var reply = new ChatResponder().Respond(Prefs(), "deck", null, null, Full() with { Deck = new ChatDeck(longName, "AAEC", "PRIEST", "standard") }, T0);
        Assert.Equal(ChatFormat.MaxGraphemes, new StringInfo(reply.Text!).LengthInTextElements);
        Assert.EndsWith("…", reply.Text, StringComparison.Ordinal);

        // Flags are 2 code points / 8 bytes per grapheme: 400 of them pass the grapheme limit but not the byte limit.
        var flags = string.Concat(Enumerable.Repeat("🇦🇷", 400));
        var fitted = ChatFormat.Fit(flags);
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= ChatFormat.MaxUtf8Bytes);
        Assert.True(new StringInfo(fitted).LengthInTextElements <= ChatFormat.MaxGraphemes);
        Assert.EndsWith("…", fitted, StringComparison.Ordinal);

        Assert.Equal("a b", ChatFormat.Fit("  a \n\n b  "));
        Assert.Equal("—", ChatFormat.Fit("   "));
    }

    [Fact]
    public void Numbers_and_streaks_follow_the_language()
    {
        Assert.Equal("1.234.567", ChatFormat.Number("es", 1234567));
        Assert.Equal("1,234,567", ChatFormat.Number("en", 1234567));
        Assert.Equal("-12", ChatFormat.Number("es", -12));
        Assert.Equal("67 %", ChatFormat.Percent("es", 2.0 / 3));
        Assert.Equal("67%", ChatFormat.Percent("en", 2.0 / 3));
        Assert.Equal("—", ChatFormat.Percent("en", null));
        Assert.Equal(string.Empty, ChatTexts.StreakText("es", 1));
        Assert.Equal(" · 2 derrotas seguidas", ChatTexts.StreakText("es", -2));
        Assert.Equal(" · 4 losses in a row", ChatTexts.StreakText("en", -4));
        Assert.Equal("Caballero de la Muerte", ChatTexts.ClassName("es", "DEATHKNIGHT"));
        Assert.Equal("Demon Hunter", ChatTexts.ClassName("en", "demonhunter"));
        Assert.Equal("Bronce 10", ChatTexts.RankLabel("es", new ChatRank(1, 1, 0, 0, "standard")));
        Assert.Equal("Platinum", ChatTexts.RankLabel("en", new ChatRank(4, 0, 0, 0, "standard")));
    }

    [Fact]
    public void Language_falls_back_to_the_system_when_unset_or_invalid()
    {
        var system = ChatFormat.SystemLanguage();
        Assert.Contains(system, ChatTexts.Languages);
        Assert.Equal(system, ChatResponder.ResolveLanguage(new ChatPrefs { Language = null }, null));
        Assert.Equal(system, ChatResponder.ResolveLanguage(new ChatPrefs { Language = "es" }, "fr"));
        Assert.Equal("en", ChatResponder.ResolveLanguage(new ChatPrefs { Language = "es" }, "EN"));
        Assert.Equal("es", ChatResponder.ResolveLanguage(new ChatPrefs { Language = "es" }, null));
    }
}
