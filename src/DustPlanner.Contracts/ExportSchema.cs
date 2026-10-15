namespace DustPlanner.Contracts;

/// <summary>
/// Constants that define the on-disk contract between the HDT exporter and the core.
/// </summary>
/// <remarks>
/// JSON conventions (both sides must honour them):
/// <list type="bullet">
///   <item>Property names are <c>snake_case</c> (Newtonsoft <c>SnakeCaseNamingStrategy</c> on the exporter,
///   <c>JsonNamingPolicy.SnakeCaseLower</c> in the core). C# names therefore avoid acronyms in caps and digits.</item>
///   <item>Dates are ISO 8601 with offset (<c>DateTimeOffset</c>).</item>
///   <item>Nulls are written explicitly; a missing optional value is <c>null</c>, never a default number.</item>
///   <item>Dictionary keys are written as strings even when the C# key is an <c>int</c> (dbfId).</item>
/// </list>
/// </remarks>
public static class ExportSchema
{
    /// <summary>Bump when a breaking change is made to any DTO in this assembly.</summary>
    public const int Version = 1;

    public const string CollectionKind = "collection";
    public const string DecksKind = "decks";
    public const string RankKind = "rank";
    public const string DiagnosticsKind = "diagnostics";

    public const string CollectionFileName = "collection.json";
    public const string DecksFileName = "decks.json";
    public const string RankFileName = "rank.json";
    public const string DiagnosticsFileName = "diagnostics.json";
    public const string HistoryFolderName = "history";

    /// <summary>
    /// Length of the per-card count array as produced by HDT's <c>Collection</c> class:
    /// <c>[count_p0, count_p1, count_p2, count_p3, trial_p0, trial_p1, trial_p2, trial_p3]</c>
    /// where <c>pN</c> is HearthMirror's <c>PremiumType</c> N.
    /// </summary>
    public const int CountArrayLength = 8;

    /// <summary>Number of premium slots in the count array (indexes 0..3 are owned copies, 4..7 are trial copies).</summary>
    public const int PremiumSlotCount = 4;

    /// <summary>PremiumType 0 — confirmed by HDT source to be the regular (non-golden) copy.</summary>
    public const int PremiumNormal = 0;

    /// <summary>PremiumType 1 — confirmed by HDT source to be the golden copy.</summary>
    public const int PremiumGolden = 1;

    /// <summary>
    /// PremiumType 2 and 3 exist in HDT's array but their meaning (signature / diamond) is NOT confirmed.
    /// The core treats copies in these slots as "unknown value" until the user confirms the mapping
    /// with the exporter's diagnostics output.
    /// </summary>
    public const int PremiumUnknownA = 2;

    /// <inheritdoc cref="PremiumUnknownA"/>
    public const int PremiumUnknownB = 3;
}
