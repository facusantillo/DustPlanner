using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Planner;

public sealed record PlanRequest(
    DeckCode TargetDeck,
    MissingReport Missing,
    CollectionSnapshot Collection,
    CardCatalog Catalog,
    GameRules Rules,
    UserPrefs Prefs,
    IReadOnlyList<ProtectedDeck> SavedDecks,
    MetaSignal Meta,
    Refunds Refunds,
    DateOnly AsOf)
{
    /// <summary>Plan for this much dust instead of the deck's shortfall.</summary>
    public int? NeedOverride { get; init; }

    /// <summary>Plan even when no saved decks are known (the "in use" protection is then blind).</summary>
    public bool AllowNoDecks { get; init; }
}

public sealed record PlanAlternative(string Label, OptimizerResult Result);

public sealed record DisenchantPlan(
    int Need,
    OptimizerResult? Main,
    IReadOnlyList<PlanAlternative> Alternatives,
    CandidatePool Pool,
    IReadOnlyList<ExcludedCopies> NotableExclusions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    int ProtectedDeckCount)
{
    public bool HasPlan => Main is { Feasible: true } && Need > 0;

    public const string PacksWarning = "Open your pending packs before disenchanting: duplicate protection looks at your current collection.";
}

/// <summary>Builds the disenchant recommendation for a target deck.</summary>
public static class DisenchantPlanner
{
    public static DisenchantPlan Plan(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string> { PacksLine(request.Collection) };
        var blockers = new List<string>();

        var need = request.NeedOverride ?? request.Missing.Shortfall;
        if (need <= 0)
        {
            blockers.Add(request.NeedOverride is null
                ? $"You already have the {request.Missing.TotalCraftCost:N0} dust this deck needs ({request.Collection.Dust:N0} available)."
                : "Nothing to plan for: the requested dust amount is zero.");
        }

        var age = DateTimeOffset.UtcNow - request.Collection.TakenAt;
        if (age.TotalDays > request.Prefs.MaxSnapshotAgeDays)
        {
            blockers.Add($"Collection snapshot is {age.Days} days old (limit {request.Prefs.MaxSnapshotAgeDays} days in the preferences). Export again before planning.");
        }

        var decks = new List<ProtectedDeck>(request.SavedDecks)
        {
            ProtectedDeck.FromDeckCode(request.TargetDeck, request.TargetDeck.Name ?? "target deck", ProtectedDecks.TargetOrigin, "target"),
        };
        if (request.SavedDecks.Count == 0)
        {
            if (request.Prefs.RequireKnownDecks && !request.AllowNoDecks)
            {
                blockers.Add("No saved decks are known, so cards in use cannot be protected. Export decks with the HDT plugin (visit the Play screen), pass --protect-deck <code>, or run with --allow-no-decks.");
            }
            else
            {
                warnings.Add("No saved decks known: only the target deck is protected.");
            }
        }

        if (request.Meta.DeckCount == 0)
        {
            warnings.Add("No meta decks loaded (meta.txt): Standard legendaries and epics are not protected by meta signal.");
        }

        if (!request.Rules.PremiumSlots.Confirmed)
        {
            warnings.Add("HDT premium slots 2/3 are unconfirmed: copies of unknown kind (signature / diamond) are never proposed.");
        }

        var pool = CandidateBuilder.Build(request.Collection, request.Catalog, request.Rules, request.Prefs, decks, request.Meta, request.Refunds, request.AsOf);
        var notable = pool.Excluded
            .Where(e => e.Card is not null && (e.DustEach ?? 0) >= 100)
            .OrderByDescending(e => (e.DustEach ?? 0) * e.Copies)
            .ThenBy(e => e.Card!.Name, StringComparer.Ordinal)
            .Take(12)
            .ToList();

        if (blockers.Count > 0)
        {
            return new DisenchantPlan(need, null, Array.Empty<PlanAlternative>(), pool, notable, warnings, blockers, request.SavedDecks.Count);
        }

        var main = DustOptimizer.Solve(pool.Groups, need, request.Prefs.Optimizer);
        if (!main.Feasible)
        {
            blockers.Add($"Not enough dust in disenchantable cards: candidates add up to {pool.TotalDust:N0}, you need {need:N0}. Loosen the protections in the preferences or wait for more cards.");
            return new DisenchantPlan(need, main, Array.Empty<PlanAlternative>(), pool, notable, warnings, blockers, request.SavedDecks.Count);
        }

        var alternatives = new List<PlanAlternative>();
        if (request.Prefs.Optimizer.Alternatives > 0 && main.Picks.Count > 0)
        {
            var mostPainful = main.Picks.OrderByDescending(p => p.Group.PainEach).ThenByDescending(p => p.Dust).First();
            if (mostPainful.Group.PainEach > 0)
            {
                var without = DustOptimizer.Solve(pool.Groups.Where(g => g != mostPainful.Group).ToList(), need, request.Prefs.Optimizer);
                AddAlternative(alternatives, $"without {mostPainful.Group.Card.Name} ({CandidateBuilder.KindName(mostPainful.Group.Kind)})", without, main);
            }
        }

        if (request.Prefs.Optimizer.Alternatives > 1 && main.Picks.Any(p => p.Group.Kind != PremiumKind.Normal))
        {
            var regularOnly = DustOptimizer.Solve(pool.Groups.Where(g => g.Kind == PremiumKind.Normal).ToList(), need, request.Prefs.Optimizer);
            AddAlternative(alternatives, "regular copies only", regularOnly, main);
        }

        return new DisenchantPlan(need, main, alternatives, pool, notable, warnings, blockers, request.SavedDecks.Count);
    }

    private static void AddAlternative(List<PlanAlternative> alternatives, string label, OptimizerResult result, OptimizerResult main)
    {
        if (!result.Feasible || SamePicks(result, main) || alternatives.Any(a => SamePicks(a.Result, result)))
        {
            return;
        }

        alternatives.Add(new PlanAlternative(label, result));
    }

    private static bool SamePicks(OptimizerResult a, OptimizerResult b) =>
        a.Picks.Count == b.Picks.Count
        && a.Picks.All(pa => b.Picks.Any(pb => pb.Group == pa.Group && pb.Copies == pa.Copies));

    private static string PacksLine(CollectionSnapshot collection) =>
        collection.UnopenedPacks is { } n
            ? $"{DisenchantPlan.PacksWarning} You have {n} unopened pack(s)."
            : $"{DisenchantPlan.PacksWarning} (Unopened pack count unknown: no HDT API exposes it.)";
}
