using System.Globalization;
using System.Text;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;

namespace DustPlanner.Cli.Rendering;

internal static class MissingReportRenderer
{
    public static string Render(MissingReport report, CollectionSnapshot collection, CardCatalog catalog)
    {
        var sb = new StringBuilder();
        var title = report.DeckName ?? "(unnamed deck)";
        var cls = report.DeckClass is null ? string.Empty : " · " + Capitalize(report.DeckClass);
        sb.AppendLine(CultureInfo.InvariantCulture, $"DustPlanner · target deck: \"{title}\"{cls} · {report.Deck.Format} · {report.Deck.TotalCards} cards");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Collection: {collection.BattleTag ?? collection.AccountKey} · snapshot {collection.TakenAt.ToLocalTime():yyyy-MM-dd HH:mm} ({collection.Source}) · catalog build {catalog.Build}");
        sb.AppendLine();

        if (report.Complete)
        {
            sb.AppendLine("You own every card in this deck.");
        }
        else
        {
            sb.AppendLine("MISSING");
            var table = new TextTable().AddColumn("card").AddColumn("rarity").AddColumn("class").AddColumn("set").AddColumn("copies", true).AddColumn("dust", true);
            foreach (var line in report.MissingCraftable)
            {
                table.AddRow(Name(line), Capitalize(line.Card.Rarity), Capitalize(line.Card.CardClass), line.Card.Set, line.Missing, line.CraftCostMissing);
            }

            table.AddRow(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "-----");
            table.AddRow("Total to craft", string.Empty, string.Empty, string.Empty, string.Empty, report.TotalCraftCost);
            table.AddRow("Dust available", string.Empty, string.Empty, string.Empty, string.Empty, report.DustAvailable);
            table.AddRow("Still needed", string.Empty, string.Empty, string.Empty, string.Empty, report.Shortfall);
            sb.Append(table.Render());

            var notCraftable = report.MissingNotCraftable.ToList();
            if (notCraftable.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("NOT CRAFTABLE (unlock in game: Core set / free cards / rewards)");
                foreach (var line in notCraftable)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {Name(line)} ×{line.Missing} — {line.Card.Set}");
                }
            }

            var unknownCraft = report.MissingUnknownCraftability.ToList();
            if (unknownCraft.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("UNKNOWN CRAFTABILITY (set not in data/non-craftable.json; not counted)");
                foreach (var line in unknownCraft)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {Name(line)} ×{line.Missing} — {line.Card.Set}");
                }
            }

            if (report.UnknownCards.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("UNKNOWN CARD IDS (not in catalog)");
                foreach (var card in report.UnknownCards)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  dbfId {card.DbfId} ×{card.Count}");
                }
            }
        }

        if (report.Companions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("INCLUDED WITH ANOTHER CARD (deck slots that come with a Fabled card; nothing to craft)");
            foreach (var c in report.Companions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {c.Companion.Name} ×{c.Count} — comes with {c.Parent.Name}");
            }
        }

        if (report.Rotation.Cards.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"ROTATES AT NEXT ROTATION ({report.Rotation.NextRotation}) · craft dust at risk: {report.Rotation.DustAtRisk:N0}");
            foreach (var line in report.Rotation.Cards)
            {
                var risk = line.CraftCostMissing is { } d ? $"craft {d:N0}" : (line.Missing > 0 ? "not craftable" : "owned");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {Name(line)} ×{line.Needed} — {line.Card.Set} — {risk}");
            }
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine();
            foreach (var warning in report.Warnings)
            {
                sb.AppendLine("⚠ " + warning);
            }
        }

        return sb.ToString();
    }

    private static string Name(DeckCardStatus line) => line.InSideboard ? line.Card.Name + " (sideboard)" : line.Card.Name;

    internal static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant().Replace('_', ' ');
}
