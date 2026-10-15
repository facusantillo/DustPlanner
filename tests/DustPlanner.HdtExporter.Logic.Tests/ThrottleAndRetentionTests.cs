using DustPlanner.HdtExporter.Logic;

namespace DustPlanner.HdtExporter.Logic.Tests;

public class ThrottleAndRetentionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Throttle_skips_unchanged_content_and_too_frequent_runs_but_never_manual_exports()
    {
        var throttle = new ExportThrottle(TimeSpan.FromSeconds(30));

        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0, "h1", manual: false));
        throttle.MarkRan(T0, "h1");

        Assert.Equal(ThrottleDecision.SkipUnchanged, throttle.Decide(T0.AddMinutes(5), "h1", manual: false));
        Assert.Equal(ThrottleDecision.SkipTooSoon, throttle.Decide(T0.AddSeconds(10), "h2", manual: false));
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0.AddSeconds(31), "h2", manual: false));
        Assert.Equal(ThrottleDecision.Run, throttle.Decide(T0.AddSeconds(1), "h1", manual: true));
        Assert.Equal(ThrottleDecision.SkipTooSoon, throttle.Decide(T0.AddSeconds(1), null, manual: false));
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
