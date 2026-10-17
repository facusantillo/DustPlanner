using DustPlanner.Contracts;
using DustPlanner.HdtExporter.Logic;

namespace DustPlanner.HdtExporter.Logic.Tests;

public class ActiveDeckExportBuilderTests
{
    private static ExportMeta Meta(string trigger) => new() { Kind = ExportSchema.ActiveDeckKind, Trigger = trigger, ExportedAt = new DateTimeOffset(2026, 10, 18, 18, 31, 5, TimeSpan.Zero) };

    [Fact]
    public void Build_normalises_class_and_format_the_way_the_tracker_stores_them()
    {
        var input = new ActiveDeckInput { Name = "Priest Control", Deckstring = "AAECAa0G", HdtDeckId = "6f0c", CardClass = "Priest", Format = "FT_STANDARD" };

        var export = ActiveDeckExportBuilder.Build(input, Meta("game_start"));

        Assert.Equal("game_start", export.Meta.Trigger);
        Assert.NotNull(export.Deck);
        Assert.Equal("PRIEST", export.Deck.CardClass);
        Assert.Equal(DeckFormats.Standard, export.Deck.Format);
        Assert.Equal("6f0c", export.Deck.HdtDeckId);
    }

    [Theory]
    [InlineData("FT_WILD", "wild")]
    [InlineData("wild", "wild")]
    [InlineData("Classic", "classic")]
    [InlineData("FT_TWIST", "twist")]
    [InlineData("FT_UNKNOWN", null)]
    [InlineData("7", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Unknown_formats_become_null_instead_of_a_guess(string? raw, string? expected) =>
        Assert.Equal(expected, ActiveDeckExportBuilder.NormaliseFormat(raw));

    [Fact]
    public void No_active_deck_exports_a_null_deck_with_a_stable_hash()
    {
        var export = ActiveDeckExportBuilder.Build(null, Meta("mode_changed:HUB"));

        Assert.Null(export.Deck);
        Assert.Equal(ActiveDeckExportBuilder.ContentHash(null), ActiveDeckExportBuilder.ContentHash(export.Deck));
        Assert.NotEqual(ActiveDeckExportBuilder.ContentHash(null), ActiveDeckExportBuilder.ContentHash(new ActiveDeck { Name = "x", Deckstring = "y" }));
    }

    [Fact]
    public void Hash_ignores_meta_and_changes_with_any_consumer_visible_field()
    {
        var a = ActiveDeckExportBuilder.Build(new ActiveDeckInput { Name = "A", Deckstring = "D", HdtDeckId = "1", CardClass = "Priest", Format = "FT_STANDARD" }, Meta("game_start"));
        var b = ActiveDeckExportBuilder.Build(new ActiveDeckInput { Name = "A", Deckstring = "D", HdtDeckId = "1", CardClass = "Priest", Format = "FT_STANDARD" }, Meta("manual"));
        Assert.Equal(ActiveDeckExportBuilder.ContentHash(a.Deck), ActiveDeckExportBuilder.ContentHash(b.Deck));

        var renamed = ActiveDeckExportBuilder.Build(new ActiveDeckInput { Name = "B", Deckstring = "D", HdtDeckId = "1", CardClass = "Priest", Format = "FT_STANDARD" }, Meta("manual"));
        var otherDeck = ActiveDeckExportBuilder.Build(new ActiveDeckInput { Name = "A", Deckstring = "E", HdtDeckId = "1", CardClass = "Priest", Format = "FT_STANDARD" }, Meta("manual"));
        var otherId = ActiveDeckExportBuilder.Build(new ActiveDeckInput { Name = "A", Deckstring = "D", HdtDeckId = "2", CardClass = "Priest", Format = "FT_STANDARD" }, Meta("manual"));
        var hashes = new[] { a, renamed, otherDeck, otherId }.Select(e => ActiveDeckExportBuilder.ContentHash(e.Deck)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, hashes.Count);
    }

    [Fact]
    public void Throttle_skips_an_unchanged_active_deck_but_never_a_manual_export()
    {
        var throttle = new ExportThrottle(TimeSpan.FromSeconds(30));
        var t0 = new DateTimeOffset(2026, 10, 18, 18, 0, 0, TimeSpan.Zero);
        var deck = ActiveDeckExportBuilder.Build(new ActiveDeckInput { Name = "A", Deckstring = "D" }, Meta("game_start")).Deck;
        var hash = ActiveDeckExportBuilder.ContentHash(deck);

        Assert.Equal(ThrottleDecision.Run, throttle.Decide(t0, hash, manual: false));
        throttle.MarkRan(t0, hash);
        // Next game with the same deck a second later: nothing to write.
        Assert.Equal(ThrottleDecision.SkipUnchanged, throttle.Decide(t0.AddSeconds(1), hash, manual: false));
        // Deck switched one second later: written even though the interval has not elapsed.
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(t0.AddSeconds(1), ActiveDeckExportBuilder.ContentHash(null), manual: false));
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(t0.AddSeconds(1), hash, manual: true));
    }
}
