using System.Collections;

namespace DustPlanner.Core.Planner;

/// <summary>Copies of one candidate group chosen by the optimizer.</summary>
public sealed record PlanPick(CandidateGroup Group, int Copies)
{
    public int Dust => Copies * Group.DustEach;

    public int Pain => Copies * Group.PainEach;
}

public sealed record OptimizerResult(IReadOnlyList<PlanPick> Picks, int Target, int TotalDust, int TotalPain, int TotalCopies, bool Feasible)
{
    public int Surplus => Math.Max(0, TotalDust - Target);

    /// <summary>Scaled objective actually minimised: pain + λ·surplus + μ·copies (×100, integer).</summary>
    public long Objective(OptimizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return DustOptimizer.Objective(TotalPain, Surplus, TotalCopies, settings);
    }
}

/// <summary>
/// Exact minimum-pain cover: pick copies whose dust sums to at least the target while minimising
/// pain + λ·surplus + μ·copies. Dynamic programming over dust in 5-point units (every in-game value is a
/// multiple of 5), with per-copy items; parent bits reconstruct the choice.
/// </summary>
public static class DustOptimizer
{
    private const int Unit = 5;
    private const int Scale = 100;

    public static OptimizerResult Solve(IReadOnlyList<CandidateGroup> groups, int target, OptimizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(settings);
        if (target <= 0)
        {
            return new OptimizerResult(Array.Empty<PlanPick>(), target, 0, 0, 0, Feasible: true);
        }

        var items = Expand(groups);
        if (items.Sum(i => (long)i.Dust) < target)
        {
            return new OptimizerResult(Array.Empty<PlanPick>(), target, 0, 0, 0, Feasible: false);
        }

        var maxValue = items.Max(i => i.Dust);
        var targetUnits = (target + Unit - 1) / Unit;
        var states = targetUnits + maxValue / Unit + 1;      // enough room for any overshoot of one item
        var lambda = settings.SurplusPenaltyPerDust * Scale;   // per dust point
        var mu = (long)Math.Round(settings.PerCopyPenalty * Scale);

        var best = new long[states];
        Array.Fill(best, long.MaxValue);
        best[0] = 0;
        var taken = new BitArray[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var units = item.Dust / Unit;
            var cost = (long)item.Pain * Scale + mu;
            var bits = new BitArray(states);
            for (var s = states - 1; s >= 0; s--)
            {
                var from = s - units;
                if (from < 0)
                {
                    break;
                }

                if (best[from] == long.MaxValue)
                {
                    continue;
                }

                var candidate = best[from] + cost;
                if (candidate < best[s])
                {
                    best[s] = candidate;
                    bits[s] = true;
                }
            }

            taken[i] = bits;
        }

        var bestState = -1;
        var bestObjective = long.MaxValue;
        for (var s = targetUnits; s < states; s++)
        {
            if (best[s] == long.MaxValue)
            {
                continue;
            }

            var surplus = s * Unit - target;
            var objective = best[s] + (long)Math.Round(surplus * lambda);
            if (objective < bestObjective)
            {
                bestObjective = objective;
                bestState = s;
            }
        }

        if (bestState < 0)
        {
            return new OptimizerResult(Array.Empty<PlanPick>(), target, 0, 0, 0, Feasible: false);
        }

        // Reconstruct: walk items backwards; an item is in the solution when its bit is set for the current state.
        var chosen = new Dictionary<CandidateGroup, int>();
        var state = bestState;
        for (var i = items.Count - 1; i >= 0 && state > 0; i--)
        {
            if (taken[i][state])
            {
                chosen[items[i].Group] = chosen.GetValueOrDefault(items[i].Group) + 1;
                state -= items[i].Dust / Unit;
            }
        }

        var picks = chosen.Select(p => new PlanPick(p.Key, p.Value))
            .OrderBy(p => p.Group.PainEach).ThenByDescending(p => p.Group.DustEach).ThenBy(p => p.Group.Card.Name, StringComparer.Ordinal)
            .ToList();
        return new OptimizerResult(picks, target, picks.Sum(p => p.Dust), picks.Sum(p => p.Pain), picks.Sum(p => p.Copies), Feasible: true);
    }

    /// <summary>Reference greedy: take copies by ascending pain-per-dust until the target is met. Used by tests as a lower bar.</summary>
    public static OptimizerResult Greedy(IReadOnlyList<CandidateGroup> groups, int target, OptimizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (target <= 0)
        {
            return new OptimizerResult(Array.Empty<PlanPick>(), target, 0, 0, 0, Feasible: true);
        }

        var picks = new Dictionary<CandidateGroup, int>();
        var total = 0;
        foreach (var item in Expand(groups).OrderBy(i => (double)i.Pain / i.Dust).ThenByDescending(i => i.Dust))
        {
            if (total >= target)
            {
                break;
            }

            picks[item.Group] = picks.GetValueOrDefault(item.Group) + 1;
            total += item.Dust;
        }

        var list = picks.Select(p => new PlanPick(p.Key, p.Value)).ToList();
        return new OptimizerResult(list, target, total, list.Sum(p => p.Pain), list.Sum(p => p.Copies), total >= target);
    }

    internal static long Objective(int pain, int surplus, int copies, OptimizerSettings settings) =>
        (long)pain * Scale + (long)Math.Round(surplus * settings.SurplusPenaltyPerDust * Scale) + (long)Math.Round(copies * settings.PerCopyPenalty * Scale);

    private static List<Item> Expand(IReadOnlyList<CandidateGroup> groups)
    {
        var items = new List<Item>();
        foreach (var group in groups)
        {
            if (group.DustEach <= 0 || group.DustEach % Unit != 0)
            {
                throw new InvalidOperationException($"Dust value {group.DustEach} for {group.Card.Name} is not a positive multiple of {Unit}.");
            }

            for (var i = 0; i < group.Copies; i++)
            {
                items.Add(new Item(group, group.DustEach, group.PainEach));
            }
        }

        return items;
    }

    private sealed record Item(CandidateGroup Group, int Dust, int Pain);
}
