using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Json;

namespace DustPlanner.Core.Planner;

/// <summary>A deck whose cards must never be proposed for disenchanting.</summary>
public sealed record ProtectedDeck(string Name, string Origin, string ExternalId, IReadOnlyDictionary<int, int> Copies)
{
    public string Key => Origin + ":" + ExternalId;

    public int CopiesOf(int dbfId) => Copies.GetValueOrDefault(dbfId);

    public static ProtectedDeck FromDeckCode(DeckCode deck, string name, string origin, string externalId)
    {
        ArgumentNullException.ThrowIfNull(deck);
        var copies = new Dictionary<int, int>();
        foreach (var card in deck.Cards)
        {
            copies[card.DbfId] = copies.GetValueOrDefault(card.DbfId) + card.Count;
        }

        foreach (var side in deck.Sideboards)
        {
            copies[side.DbfId] = copies.GetValueOrDefault(side.DbfId) + side.Count;
        }

        return new ProtectedDeck(name, origin, externalId, copies);
    }
}

public static class ProtectedDecks
{
    public const string TargetOrigin = "target";
    public const string ManualOrigin = "manual";

    /// <summary>Reads the exporter's <c>decks.json</c>; archived HDT decks and excluded decks are dropped.</summary>
    public static IReadOnlyList<ProtectedDeck> FromExport(DecksExport export, UserPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(prefs);
        var result = new List<ProtectedDeck>();
        foreach (var deck in export.Decks)
        {
            if (prefs.IgnoreArchivedDecks && deck.Archived)
            {
                continue;
            }

            var copies = new Dictionary<int, int>();
            foreach (var card in deck.Cards.Where(c => c.DbfId > 0))
            {
                copies[card.DbfId] = copies.GetValueOrDefault(card.DbfId) + card.Count;
            }

            foreach (var card in deck.Sideboards.SelectMany(s => s.Cards).Where(c => c.DbfId > 0))
            {
                copies[card.DbfId] = copies.GetValueOrDefault(card.DbfId) + card.Count;
            }

            var protectedDeck = new ProtectedDeck(deck.Name, deck.Origin, deck.ExternalId, copies);
            if (IsExcluded(protectedDeck, prefs))
            {
                continue;
            }

            result.Add(protectedDeck);
        }

        return result;
    }

    public static IReadOnlyList<ProtectedDeck> FromExportFile(string path, UserPrefs prefs)
    {
        var export = JsonSerializer.Deserialize<DecksExport>(File.ReadAllText(path), CoreJson.Options)
                     ?? throw new InvalidDataException("Empty decks export.");
        return FromExport(export, prefs);
    }

    public static bool IsExcluded(ProtectedDeck deck, UserPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(prefs);
        return prefs.ExcludedDecks.Any(x => string.Equals(x, deck.Key, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(x, deck.Name, StringComparison.OrdinalIgnoreCase));
    }
}
