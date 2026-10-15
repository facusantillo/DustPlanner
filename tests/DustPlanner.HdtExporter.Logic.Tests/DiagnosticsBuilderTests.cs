using DustPlanner.Contracts;
using DustPlanner.HdtExporter.Logic;

namespace DustPlanner.HdtExporter.Logic.Tests;

public class DiagnosticsBuilderTests
{
    private static readonly Dictionary<int, CardInfo> Cards = new()
    {
        [1001] = new CardInfo(1001, "CS1_130", "Holy Smite", "FREE", "CORE"),
        [1002] = new CardInfo(1002, "EX1_001", "Shadow Priest", "COMMON", "EXPERT1"),
        [1003] = new CardInfo(1003, "TTN_900", "Big Titan", "LEGENDARY", "TITANS"),
        [1004] = new CardInfo(1004, "TTN_901", "Shiny Drake", "EPIC", "TITANS"),
        [1005] = new CardInfo(1005, "CORE_EX1_002", "Core Rare", "RARE", "CORE"),
    };

    private static CardInfo? Resolve(int dbfId) => Cards.TryGetValue(dbfId, out var c) ? c : null;

    [Fact]
    public void Samples_prefer_cards_with_unconfirmed_premium_slots()
    {
        var input = new CollectionInput
        {
            Dust = 0,
            HdtCards = new Dictionary<int, int[]>
            {
                [1001] = new[] { 2, 0, 0, 0, 0, 0, 0, 0 },
                [1002] = new[] { 1, 1, 0, 0, 0, 0, 0, 0 },
                [1003] = new[] { 0, 0, 1, 0, 0, 0, 0, 0 },
                [1004] = new[] { 1, 0, 0, 1, 0, 0, 0, 0 },
                [1005] = new[] { 2, 0, 0, 0, 0, 0, 0, 0 },
            },
            RawCards = new[]
            {
                new MirrorCardEntry("TTN_900", 1003, 2, 1, 0),
                new MirrorCardEntry("TTN_901", 1004, 0, 1, 0),
                new MirrorCardEntry("TTN_901", 1004, 3, 1, 0),
                new MirrorCardEntry("EX1_001", 1002, 0, 1, 0),
                new MirrorCardEntry("EX1_001", 1002, 1, 1, 0),
            },
        };

        var result = DiagnosticsBuilder.Build(input, Resolve, new ExportMeta());

        Assert.Equal(ExportSchema.DiagnosticsKind, result.Meta.Kind);
        Assert.Equal(1003, result.Samples[0].DbfId);
        Assert.Equal(1004, result.Samples[1].DbfId);
        Assert.Equal("Big Titan", result.Samples[0].Name);
        Assert.Single(result.Samples[0].MirrorEntries);
        Assert.Equal(2, result.Samples[0].MirrorEntries[0].PremiumType);
        Assert.Contains(result.Samples, s => s.DbfId == 1002); // golden reference
        Assert.Contains(result.Samples, s => s.DbfId == 1001); // regular reference

        Assert.Equal(new[] { 0, 1, 2, 3 }, result.PremiumTypes.Select(p => p.PremiumType).ToArray());
        Assert.Equal(2, result.PremiumTypes.Single(p => p.PremiumType == 0).DistinctCards);

        Assert.True(result.CoreSetPresent);
        Assert.Equal(2, result.CoreSetDistinctCards);
        Assert.Contains("Holy Smite", result.CoreSetSampleNames);
        Assert.DoesNotContain(result.Notes, n => n.Contains("cannot be confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_raw_entries_stats_come_from_the_hdt_array_and_a_note_is_added()
    {
        var input = new CollectionInput
        {
            HdtCards = new Dictionary<int, int[]> { [1002] = new[] { 1, 1, 0, 0, 1, 0, 0, 0 } },
        };

        var result = DiagnosticsBuilder.Build(input, Resolve, new ExportMeta());

        Assert.Equal(4, result.PremiumTypes.Count);
        Assert.Equal(1, result.PremiumTypes[0].TotalCopies);
        Assert.Equal(1, result.PremiumTypes[0].TrialCopies);
        Assert.Contains(result.Notes, n => n.Contains("Raw HearthMirror collection was not available", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("cannot be confirmed", StringComparison.Ordinal));
        Assert.False(result.CoreSetPresent);
    }

    [Fact]
    public void Sample_count_is_capped_and_reported()
    {
        var cards = new Dictionary<int, int[]>();
        for (var i = 0; i < 50; i++)
        {
            cards[2000 + i] = new[] { 0, 0, 1, 0, 0, 0, 0, 0 };
        }

        var result = DiagnosticsBuilder.Build(new CollectionInput { HdtCards = cards }, _ => null, new ExportMeta(), maxSamples: 10);

        Assert.Equal(10, result.Samples.Count);
        Assert.Contains(result.Notes, n => n.StartsWith("50 cards have copies", StringComparison.Ordinal));
        Assert.Null(result.Samples[0].Name);
    }
}
