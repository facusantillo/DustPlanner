using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;
using DustPlanner.LogTracker;
using DustPlanner.Store;

namespace DustPlanner.App.Reports;

/// <summary>
/// JSON projections shared by the CLI (<c>--json</c>) and the MCP server, so both speak the same snake_case contract
/// (serialized with <see cref="Core.Json.CoreJson.Options"/>).
/// </summary>
public sealed record MissingReportJson(
    string? DeckName,
    string? DeckClass,
    string Format,
    int TotalCards,
    string Account,
    DateTimeOffset SnapshotAt,
    string SnapshotSource,
    string CatalogBuild,
    DateOnly AsOf,
    int DustAvailable,
    int TotalCraftCost,
    int Shortfall,
    bool Complete,
    IReadOnlyList<MissingCardJson> Missing,
    IReadOnlyList<MissingCardJson> NotCraftable,
    IReadOnlyList<MissingCardJson> UnknownCraftability,
    IReadOnlyList<UnknownDeckCard> UnknownCards,
    RotationJson Rotation,
    IReadOnlyList<string> Warnings)
{
    public static MissingReportJson From(MissingReport report, CollectionSnapshot collection, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(catalog);
        return new(
            report.DeckName,
            report.DeckClass,
            report.Deck.Format.ToString(),
            report.Deck.TotalCards,
            collection.BattleTag ?? collection.AccountKey,
            collection.TakenAt,
            collection.Source,
            catalog.Build,
            report.AsOf,
            report.DustAvailable,
            report.TotalCraftCost,
            report.Shortfall,
            report.Complete,
            report.MissingCraftable.Select(MissingCardJson.From).ToList(),
            report.MissingNotCraftable.Select(MissingCardJson.From).ToList(),
            report.MissingUnknownCraftability.Select(MissingCardJson.From).ToList(),
            report.UnknownCards,
            new RotationJson(report.Rotation.NextRotation, report.Rotation.DustAtRisk, report.Rotation.Cards.Select(MissingCardJson.From).ToList()),
            report.Warnings);
    }
}

public sealed record MissingCardJson(int DbfId, string CardId, string Name, string Rarity, string CardClass, string Set, int Needed, int Owned, int Missing, bool InSideboard, int? CraftCostEach, int? CraftCostMissing, bool RotatesNext)
{
    public static MissingCardJson From(DeckCardStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Card.DbfId, s.Card.CardId, s.Card.Name, s.Card.Rarity, s.Card.CardClass, s.Card.Set,
            s.Needed, s.Owned, s.Missing, s.InSideboard, s.CraftCostEach, s.CraftCostMissing, s.RotatesNext);
    }
}

public sealed record RotationJson(string NextRotation, int DustAtRisk, IReadOnlyList<MissingCardJson> Cards);

public sealed record PlanJson(
    MissingReportJson Missing,
    int Need,
    bool HasPlan,
    PlanResultJson? Main,
    IReadOnlyList<PlanAlternativeJson> Alternatives,
    IReadOnlyList<ExclusionJson> NotableExclusions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    int ProtectedDeckCount,
    int CandidateDustTotal)
{
    /// <summary>Id of the stored plan when the caller persisted it (the CLI does; the MCP server never does).</summary>
    public long? SavedPlanId { get; init; }

    public static PlanJson From(DisenchantPlan plan, MissingReport missing, CollectionSnapshot collection, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(
            MissingReportJson.From(missing, collection, catalog),
            plan.Need,
            plan.HasPlan,
            plan.Main is { Feasible: true } m ? PlanResultJson.From(m) : null,
            plan.Alternatives.Select(a => new PlanAlternativeJson(a.Label, PlanResultJson.From(a.Result))).ToList(),
            plan.NotableExclusions.Select(e => new ExclusionJson(e.DbfId, e.Card?.Name, CandidateBuilder.KindName(e.Kind), e.Copies, e.DustEach, e.Reason)).ToList(),
            plan.Warnings,
            plan.Blockers,
            plan.ProtectedDeckCount,
            plan.Pool.TotalDust);
    }

    public static PlanJson From(PlanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return From(outcome.Plan, outcome.Need.Report, outcome.Need.Collection, outcome.Need.Catalog.Catalog);
    }
}

public sealed record PlanResultJson(int TotalDust, int Surplus, int TotalPain, int TotalCopies, IReadOnlyList<PickJson> Picks)
{
    public static PlanResultJson From(OptimizerResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.TotalDust, r.Surplus, r.TotalPain, r.TotalCopies,
            r.Picks.Select(p => new PickJson(p.Group.Card.DbfId, p.Group.Card.CardId, p.Group.Card.Name, p.Group.Card.Rarity, p.Group.Card.CardClass, p.Group.Card.Set,
                CandidateBuilder.KindName(p.Group.Kind), p.Copies, p.Group.DustEach, p.Dust, p.Group.PainEach, p.Group.Category.ToString(), p.Group.Reasons, p.Group.RefundActive)).ToList());
    }
}

public sealed record PlanAlternativeJson(string Label, PlanResultJson Result);

public sealed record PickJson(int DbfId, string CardId, string Name, string Rarity, string CardClass, string Set, string Kind, int Copies, int DustEach, int DustTotal, int PainEach, string Category, IReadOnlyList<string> Reasons, bool RefundActive);

public sealed record ExclusionJson(int DbfId, string? Name, string Kind, int Copies, int? DustEach, string Reason);

/// <summary>A detected collection change (from <c>sync</c> / <c>history</c>).</summary>
public sealed record EventJson(long Id, int DbfId, string CardName, string Kind, int Delta, string EventKind, int? DustEstimate, long? PlanId, string Confidence, DateTimeOffset DetectedAt)
{
    public static EventJson From(CollectionEvent e, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(catalog);
        return new(e.Id, e.DbfId, catalog.GetByDbfId(e.DbfId)?.Name ?? $"dbfId {e.DbfId}", CandidateBuilder.KindName(e.Kind), e.Delta, e.EventKind, e.DustEstimate, e.PlanId, e.ConfidenceLevel, e.DetectedAt);
    }
}

/// <summary>Win/loss counters with the win rate over decided games.</summary>
public sealed record StatsJson(int Games, int Wins, int Losses, int Ties, int Unknown, double? WinRate)
{
    public static StatsJson From(WinRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        return new(rate.Games, rate.Wins, rate.Losses, rate.Ties, rate.Unknown, rate.Percentage);
    }
}

/// <summary>Collection snapshot summary (what <c>collection show</c> prints).</summary>
public sealed record CollectionSummaryJson(
    string Account,
    string AccountKey,
    string? Region,
    DateTimeOffset TakenAt,
    string Source,
    int Dust,
    int? Gold,
    int? UnopenedPacks,
    int DistinctCards,
    IReadOnlyDictionary<string, int> Copies,
    int CardsNotInCatalog,
    IReadOnlyList<string> UnknownSets,
    string CatalogBuild)
{
    public static CollectionSummaryJson From(CollectionSnapshot collection, CardCatalog catalog, GameRules rules)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rules);
        var byKind = Enum.GetValues<PremiumKind>().ToDictionary(k => k.ToString().ToLowerInvariant(), k => collection.Cards.Sum(c => c.Count(k)), StringComparer.Ordinal);
        var unknownSets = collection.Cards.Select(c => catalog.GetByDbfId(c.DbfId)?.Set).Where(s => s is not null && !rules.Craftability.IsSetKnown(s)).Distinct().Order().ToList();
        var notInCatalog = collection.Cards.Count(c => catalog.GetByDbfId(c.DbfId) is null);
        return new(collection.BattleTag ?? collection.AccountKey, collection.AccountKey, collection.Region, collection.TakenAt, collection.Source, collection.Dust, collection.Gold,
            collection.UnopenedPacks, collection.DistinctCards, byKind, notInCatalog, unknownSets!, catalog.Build);
    }
}
