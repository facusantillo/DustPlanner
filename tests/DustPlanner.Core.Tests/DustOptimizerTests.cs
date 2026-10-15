using DustPlanner.Core.Cards;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace DustPlanner.Core.Tests;

public class DustOptimizerTests
{
    private static readonly OptimizerSettings Settings = new();

    internal static CandidateGroup Group(string name, int copies, int dust, int pain, PremiumKind kind = PremiumKind.Normal) =>
        new(new CardDef { DbfId = name.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF, CardId = name, Name = name, Set = "BE", Rarity = Rarities.Common, CardClass = "NEUTRAL", Classes = new[] { "NEUTRAL" }, Type = "MINION" },
            kind, copies, dust, pain, PainCategory.StandardNoSignal, new[] { "test" }, true, false);

    [Fact]
    public void Picks_the_minimum_pain_cover_not_the_greedy_one()
    {
        // Greedy by pain-per-dust takes the 400-dust pain-30 card (0.075/dust) before four 100-dust pain-8 cards (0.08/dust),
        // but the exact answer for 400 dust is the four cheap cards (pain 32 vs 30?) — no: 4×8 = 32 > 30. Make it 4×7 = 28 < 30.
        var groups = new List<CandidateGroup>
        {
            Group("big", 1, 400, 30),
            Group("small", 4, 100, 7),
        };

        var result = DustOptimizer.Solve(groups, 400, Settings);

        Assert.True(result.Feasible);
        Assert.Equal(400, result.TotalDust);
        Assert.Equal(28, result.TotalPain);
        Assert.Single(result.Picks);
        Assert.Equal(4, result.Picks[0].Copies);
    }

    [Fact]
    public void Prefers_exact_cover_over_surplus_and_fewer_copies_at_equal_pain()
    {
        var groups = new List<CandidateGroup>
        {
            Group("a", 1, 100, 0),
            Group("b", 1, 400, 0),
            Group("c", 2, 50, 0),
        };

        var exact = DustOptimizer.Solve(groups, 200, Settings);
        Assert.Equal(200, exact.TotalDust); // 100 + 50 + 50 beats 400 (surplus) — λ·200 = 2.0 > μ·2 = 0.2
        Assert.Equal(3, exact.TotalCopies);

        var fewer = DustOptimizer.Solve(groups, 100, Settings);
        Assert.Equal(100, fewer.TotalDust);
        Assert.Equal(1, fewer.TotalCopies); // 100 alone beats 50 + 50
    }

    [Fact]
    public void Infeasible_when_candidates_cannot_reach_the_target_and_trivial_when_target_is_zero()
    {
        var groups = new List<CandidateGroup> { Group("a", 2, 100, 1) };

        Assert.False(DustOptimizer.Solve(groups, 300, Settings).Feasible);
        Assert.True(DustOptimizer.Solve(groups, 200, Settings).Feasible);
        var zero = DustOptimizer.Solve(groups, 0, Settings);
        Assert.True(zero.Feasible);
        Assert.Empty(zero.Picks);
    }

    [Fact]
    public void Rejects_values_that_are_not_multiples_of_five()
    {
        Assert.Throws<InvalidOperationException>(() => DustOptimizer.Solve(new[] { Group("x", 1, 7, 0) }, 5, Settings));
    }

    [Property(MaxTest = 150)]
    public Property Solve_is_feasible_iff_total_dust_reaches_target_and_never_beats_the_bound_or_loses_to_greedy(
        NonEmptyArray<PositiveInt> copies, NonEmptyArray<PositiveInt> values, NonEmptyArray<byte> pains, PositiveInt targetUnits)
    {
        var groups = copies.Get.Select((c, i) => Group("g" + i, Math.Min(c.Get, 4), 5 * Math.Min(values.Get[i % values.Get.Length].Get, 64), pains.Get[i % pains.Get.Length] % 101)).ToList();
        var target = 5 * Math.Min(targetUnits.Get, 400);
        var total = groups.Sum(g => g.Copies * g.DustEach);

        var solved = DustOptimizer.Solve(groups, target, Settings);
        var greedy = DustOptimizer.Greedy(groups, target, Settings);

        var feasibility = (solved.Feasible == (total >= target)).Label("feasible iff reachable");
        if (!solved.Feasible)
        {
            return feasibility;
        }

        var meets = (solved.TotalDust >= target).Label("meets target");
        var withinCopies = solved.Picks.All(p => p.Copies <= p.Group.Copies && p.Copies > 0).Label("respects copies");
        var consistent = (solved.TotalPain == solved.Picks.Sum(p => p.Pain) && solved.TotalDust == solved.Picks.Sum(p => p.Dust)).Label("totals consistent");
        var notWorseThanGreedy = (!greedy.Feasible || solved.Objective(Settings) <= greedy.Objective(Settings)).Label("≤ greedy objective");
        return feasibility.And(meets).And(withinCopies).And(consistent).And(notWorseThanGreedy);
    }

    [Property(MaxTest = 60)]
    public Property Solve_matches_brute_force_on_small_inputs(NonEmptyArray<PositiveInt> values, NonEmptyArray<byte> pains, PositiveInt targetUnits)
    {
        var groups = values.Get.Take(8).Select((v, i) => Group("g" + i, 1, 5 * (1 + v.Get % 8), pains.Get[i % pains.Get.Length] % 20)).ToList();
        var target = 5 * (1 + targetUnits.Get % 20);

        var solved = DustOptimizer.Solve(groups, target, Settings);

        long best = long.MaxValue;
        for (var mask = 0; mask < 1 << groups.Count; mask++)
        {
            var dust = 0;
            var pain = 0;
            var copies = 0;
            for (var i = 0; i < groups.Count; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    dust += groups[i].DustEach;
                    pain += groups[i].PainEach;
                    copies++;
                }
            }

            if (dust >= target)
            {
                best = Math.Min(best, DustOptimizer.Objective(pain, dust - target, copies, Settings));
            }
        }

        return solved.Feasible
            ? (solved.Objective(Settings) == best).Label($"exact optimum: dp {solved.Objective(Settings)} vs brute {best}")
            : (best == long.MaxValue).Label("infeasible agrees");
    }
}
