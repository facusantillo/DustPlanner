using System.Collections.Generic;

namespace DustPlanner.Contracts;

/// <summary>Snapshot of a constructed-card collection as HDT exposes it.</summary>
public sealed class CollectionExport
{
    public ExportMeta Meta { get; set; } = new ExportMeta();

    /// <summary>Arcane dust balance.</summary>
    public int Dust { get; set; }

    /// <summary>Gold balance when available (HearthMirror exposes it on the raw collection).</summary>
    public int? Gold { get; set; }

    /// <summary>
    /// Unopened pack count. Always <c>null</c> today: no HDT / HearthMirror API exposes it
    /// (verified against HDT 1.55.6 binaries and 1.59.2 source). Kept so the contract does not
    /// need a schema bump if it becomes available.
    /// </summary>
    public int? UnopenedPacks { get; set; }

    /// <summary>
    /// dbfId → count array exactly as HDT builds it (see <see cref="ExportSchema.CountArrayLength"/>).
    /// Index 0 = regular, 1 = golden, 2 and 3 = unconfirmed premium types, 4..7 = trial copies.
    /// </summary>
    public Dictionary<int, int[]> Cards { get; set; } = new Dictionary<int, int[]>();

    /// <summary>SHA-256 (hex, lowercase) of the canonical <c>dust|dbfId:counts;...</c> string; used for change detection.</summary>
    public string? ContentHash { get; set; }
}
