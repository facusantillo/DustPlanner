using DustPlanner.HdtExporter.Logic;

namespace DustPlanner.HdtExporter.Logic.Tests;

public class ThrottleAndRetentionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Throttle_skips_unchanged_content_and_too_frequent_unhashed_runs_but_never_changes_or_manual_exports()
    {
        var throttle = new ExportThrottle(TimeSpan.FromSeconds(30));

        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0, "h1", manual: false));
        throttle.MarkRan(T0, "h1");

        Assert.Equal(ThrottleDecision.SkipUnchanged, throttle.Decide(T0.AddMinutes(5), "h1", manual: false));
        // Enter the collection screen, disenchant, leave within 30 s: the changed collection must still be written.
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0.AddSeconds(10), "h2", manual: false));
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0.AddSeconds(31), "h2", manual: false));
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0.AddSeconds(1), "h1", manual: true));
        // Exports without a content hash (decks, rank) are the ones rate-limited.
        Assert.Equal(ThrottleDecision.SkipTooSoon, throttle.Decide(T0.AddSeconds(1), null, manual: false));
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0.AddSeconds(30), null, manual: false));
    }

    [Fact]
    public void History_file_names_use_utc_and_prune_oldest_first()
    {
        var name = HistoryRetention.HistoryFileName("collection", new DateTimeOffset(2026, 10, 15, 13, 52, 10, TimeSpan.FromHours(2)));
        Assert.Equal("collection-20261015-115210.json", name);

        var files = new[]
        {
            "collection-20261015-115210.json",
            "collection-20261014-080000.json",
            "collection-20261013-080000.json",
            "decks-20261001-000000.json",
            "collection-latest.json",
            "notes.txt",
        };

        var prune = HistoryRetention.FilesToPrune(files, "collection", keep: 2);

        Assert.Equal(new[] { "collection-20261013-080000.json" }, prune);
        Assert.Empty(HistoryRetention.FilesToPrune(files, "collection", keep: 3));
        Assert.Equal(3, HistoryRetention.FilesToPrune(files, "collection", keep: 0).Count);
        Assert.Empty(HistoryRetention.FilesToPrune(files, "rank", keep: 0));
    }
}

public sealed class LastAccountTests
{
    [Fact]
    public void Round_trips_the_account_and_tolerates_a_missing_tag()
    {
        var line = LastAccount.Serialize(8589934592, 42, "Tsukuru#2134");
        Assert.Equal("8589934592\t42\tTsukuru#2134", line);
        Assert.True(LastAccount.TryParse(line + "\r\n", out var hi, out var lo, out var tag));
        Assert.Equal(8589934592UL, hi);
        Assert.Equal(42UL, lo);
        Assert.Equal("Tsukuru#2134", tag);

        Assert.True(LastAccount.TryParse(LastAccount.Serialize(1, 2, null), out _, out _, out var none));
        Assert.Null(none);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("0\t0\tnobody")]
    [InlineData("-1\t5\tx")]
    [InlineData("1,5\t5\tx")]
    public void Rejects_what_it_did_not_write(string? text)
    {
        Assert.False(LastAccount.TryParse(text, out var hi, out var lo, out var tag));
        Assert.Equal(0UL, hi);
        Assert.Equal(0UL, lo);
        Assert.Null(tag);
    }
}
