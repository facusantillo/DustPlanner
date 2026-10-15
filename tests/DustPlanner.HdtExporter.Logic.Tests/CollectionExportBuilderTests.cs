using DustPlanner.Contracts;
using DustPlanner.HdtExporter.Logic;

namespace DustPlanner.HdtExporter.Logic.Tests;

public class CollectionExportBuilderTests
{
    [Fact]
    public void Hash_is_deterministic_and_order_independent()
    {
        var a = new Dictionary<int, int[]> { [2] = new[] { 1, 0, 0, 0, 0, 0, 0, 0 }, [1] = new[] { 2, 1, 0, 0, 0, 0, 0, 0 } };
        var b = new Dictionary<int, int[]> { [1] = new[] { 2, 1, 0, 0, 0, 0, 0, 0 }, [2] = new[] { 1, 0, 0, 0, 0, 0, 0, 0 } };

        Assert.Equal(CollectionExportBuilder.ComputeContentHash(100, a), CollectionExportBuilder.ComputeContentHash(100, b));
        Assert.NotEqual(CollectionExportBuilder.ComputeContentHash(100, a), CollectionExportBuilder.ComputeContentHash(101, a));
        Assert.Matches("^[0-9a-f]{64}$", CollectionExportBuilder.ComputeContentHash(0, a));
    }

    [Fact]
    public void Hash_ignores_all_zero_entries_and_short_arrays_are_padded()
    {
        var withZero = new Dictionary<int, int[]> { [1] = new[] { 2, 0 }, [9] = new int[8] };
        var without = new Dictionary<int, int[]> { [1] = new[] { 2, 0, 0, 0, 0, 0, 0, 0 } };

        Assert.Equal(CollectionExportBuilder.ComputeContentHash(5, without), CollectionExportBuilder.ComputeContentHash(5, withZero));
        Assert.Equal(new[] { 2, 0, 0, 0, 0, 0, 0, 0 }, CollectionExportBuilder.NormalizeCounts(new[] { 2, 0 }));
        Assert.Equal(8, CollectionExportBuilder.NormalizeCounts(new int[12]).Length);
        Assert.Equal(new int[8], CollectionExportBuilder.NormalizeCounts(null));
    }

    [Fact]
    public void Build_sets_kind_hash_and_leaves_unopened_packs_unknown()
    {
        var input = new CollectionInput
        {
            Dust = 640,
            Gold = 1200,
            HdtCards = new Dictionary<int, int[]> { [1001] = new[] { 2, 0, 0, 0, 0, 0, 0, 0 } },
        };

        var export = CollectionExportBuilder.Build(input, new ExportMeta { Trigger = "manual" });

        Assert.Equal(ExportSchema.CollectionKind, export.Meta.Kind);
        Assert.Equal(640, export.Dust);
        Assert.Equal(1200, export.Gold);
        Assert.Null(export.UnopenedPacks);
        Assert.Equal(CollectionExportBuilder.ComputeContentHash(640, input.HdtCards), export.ContentHash);
        Assert.NotSame(input.HdtCards[1001], export.Cards[1001]);
    }
}
