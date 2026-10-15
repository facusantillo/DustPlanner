using System.Collections.Generic;

namespace DustPlanner.Contracts;

/// <summary>
/// One-off dump that lets the user confirm, against the in-game collection, what HDT's count-array
/// indexes 2 and 3 mean and whether Core-set cards appear in the export. Nothing in the core
/// depends on this file; it exists to answer verification questions.
/// </summary>
public sealed class DiagnosticsExport
{
    public ExportMeta Meta { get; set; } = new ExportMeta();

    /// <summary>How many distinct cards / copies exist per raw HearthMirror <c>PremiumType</c>.</summary>
    public List<PremiumTypeStat> PremiumTypes { get; set; } = new List<PremiumTypeStat>();

    /// <summary>Cards that have copies with PremiumType ≥ 2, plus a few regular/golden references.</summary>
    public List<DiagnosticCardSample> Samples { get; set; } = new List<DiagnosticCardSample>();

    /// <summary>True when at least one card whose set is <c>CORE</c> appears in the collection.</summary>
    public bool CoreSetPresent { get; set; }

    public int CoreSetDistinctCards { get; set; }

    public List<string> CoreSetSampleNames { get; set; } = new List<string>();

    /// <summary>Free-form notes written by the exporter (what it could and could not read).</summary>
    public List<string> Notes { get; set; } = new List<string>();
}

public sealed class PremiumTypeStat
{
    public int PremiumType { get; set; }

    public int DistinctCards { get; set; }

    public int TotalCopies { get; set; }

    public int TrialCopies { get; set; }
}

public sealed class DiagnosticCardSample
{
    public int DbfId { get; set; }

    public string? CardId { get; set; }

    public string? Name { get; set; }

    public string? Rarity { get; set; }

    public string? Set { get; set; }

    /// <summary>The 8-int array HDT builds for this card.</summary>
    public int[] RawCounts { get; set; } = new int[ExportSchema.CountArrayLength];

    /// <summary>Raw HearthMirror entries for the card, one per PremiumType present.</summary>
    public List<RawMirrorEntry> MirrorEntries { get; set; } = new List<RawMirrorEntry>();
}

public sealed class RawMirrorEntry
{
    public int PremiumType { get; set; }

    public int Count { get; set; }

    public int TrialCount { get; set; }
}
