using System.Globalization;
using System.Text;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Planner;

namespace DustPlanner.Cli.Rendering;

internal static class PlanRenderer
{
    public static string Render(DisenchantPlan plan, MissingReport missing, CollectionSnapshot collection, CardCatalog catalog)
    {
        var sb = new StringBuilder();
        sb.Append(MissingReportRenderer.Render(missing, collection, catalog));
        sb.AppendLine();

        foreach (var warning in plan.Warnings)
        {
            sb.AppendLine("⚠ " + warning);
        }

        sb.AppendLine();
        if (!plan.HasPlan)
        {
            sb.AppendLine("NO DISENCHANT PLAN");
            foreach (var blocker in plan.Blockers)
            {
                sb.AppendLine("  ✗ " + blocker);
            }

            AppendExclusions(sb, plan);
            return sb.ToString();
        }

        var main = plan.Main!;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"DISENCHANT PLAN · need {plan.Need:N0} · gives {main.TotalDust:N0} (surplus {main.Surplus:N0}) · pain {main.TotalPain} · {main.TotalCopies} copies · {plan.ProtectedDeckCount} saved deck(s) protected");
        sb.Append(RenderPicks(main));

        foreach (var alternative in plan.Alternatives)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"ALTERNATIVE — {alternative.Label} · gives {alternative.Result.TotalDust:N0} · pain {alternative.Result.TotalPain} · {alternative.Result.TotalCopies} copies");
            sb.Append(RenderPicks(alternative.Result));
        }

        AppendExclusions(sb, plan);
        sb.AppendLine();
        sb.AppendLine("DustPlanner never disenchants anything. Do it by hand in the game's collection, then export again.");
        return sb.ToString();
    }

    private static string RenderPicks(OptimizerResult result)
    {
        var table = new TextTable().AddColumn("#", true).AddColumn("card").AddColumn("kind").AddColumn("copies", true).AddColumn("dust", true).AddColumn("pain", true).AddColumn("why");
        var i = 0;
        foreach (var pick in result.Picks)
        {
            i++;
            table.AddRow(i, $"{pick.Group.Card.Name} ({MissingReportRenderer.Capitalize(pick.Group.Card.Rarity)}, {MissingReportRenderer.Capitalize(pick.Group.Card.CardClass)})",
                CandidateBuilder.KindName(pick.Group.Kind), pick.Copies, pick.Dust, pick.Pain, string.Join(" · ", pick.Group.Reasons));
        }

        return table.Render();
    }

    private static void AppendExclusions(StringBuilder sb, DisenchantPlan plan)
    {
        if (plan.NotableExclusions.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("NOT PROPOSED (protected or without a known value)");
        foreach (var e in plan.NotableExclusions)
        {
            var value = e.DustEach is { } d ? $"{d * e.Copies:N0} dust" : "value unknown";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {e.Card!.Name} · {CandidateBuilder.KindName(e.Kind)} ×{e.Copies} · {value} — {e.Reason}");
        }

        var byReason = plan.Pool.Excluded.GroupBy(e => e.Reason).Select(g => $"{g.Sum(x => x.Copies)} copies {g.Key}").ToList();
        if (byReason.Count > 0)
        {
            sb.AppendLine("  Excluded in total: " + string.Join("; ", byReason));
        }
    }
}
