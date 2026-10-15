using System.Text.Json;
using System.Text.RegularExpressions;
using DustPlanner.Contracts;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Json;

namespace DustPlanner.App;

/// <summary>A deck the exporter captured (in-game or HDT's own), as the panel lists it. <c>Key</c> (<c>origin:external_id</c>) can be passed wherever a deck code is expected.</summary>
public sealed record DeckListItem(string Key, string Origin, string ExternalId, string Name, string? CardClass, string? Format, bool Archived, int CardCount, string? Deckstring, DateTimeOffset? LastEdited);

public sealed partial class AppServices
{
    private static readonly Regex DeckKeyPattern = new("^(?<origin>[a-z_]+):(?<id>[^\\s]+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Decks exported for the account (<c>decks.json</c> in the inbox), newest edit first, archived last.</summary>
    public IReadOnlyList<DeckListItem> ListDecks(string? account, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var export = LoadDecksExport(account);
        if (export is null)
        {
            return Array.Empty<DeckListItem>();
        }

        return export.Decks
            .Select(d => ToListItem(d, catalog))
            .OrderBy(d => d.Archived)
            .ThenByDescending(d => d.LastEdited ?? DateTimeOffset.MinValue)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Turns a deck argument into deck text: a deck code or pasted block is returned as is; a deck key
    /// (<c>hdt:&lt;id&gt;</c>, <c>ingame:&lt;id&gt;</c>, as <see cref="ListDecks"/> reports it) is resolved against the
    /// account's exported decks and encoded.
    /// </summary>
    public string ResolveDeckArgument(string deck, string? account, CardCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deck);
        ArgumentNullException.ThrowIfNull(catalog);
        var trimmed = deck.Trim();
        var match = DeckKeyPattern.Match(trimmed);
        if (!match.Success || trimmed.StartsWith("AAE", StringComparison.Ordinal))
        {
            return deck;
        }

        var export = LoadDecksExport(account) ?? throw new AppException($"No exported decks for this account; cannot resolve deck '{trimmed}'.");
        var found = export.Decks.FirstOrDefault(d =>
            string.Equals(d.Origin, match.Groups["origin"].Value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.ExternalId, match.Groups["id"].Value, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException($"Deck '{trimmed}' is not among the exported decks (run the HDT exporter again if it is new).");

        var code = ToDeckCode(found, catalog);
        if (code.Heroes.Count != 1)
        {
            throw new AppException($"Deck '{found.Name}' has no identifiable class; paste its deck code instead.");
        }

        return "### " + found.Name + "\n" + DeckstringCodec.Encode(code);
    }

    private DecksExport? LoadDecksExport(string? account)
    {
        var accounts = InboxLocator.ListAccounts(Paths.Inbox);
        var (selected, _) = InboxLocator.Select(accounts, account);
        if (selected is null)
        {
            return null;
        }

        var file = Path.Combine(selected.Directory, ExportSchema.DecksFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        return JsonSerializer.Deserialize<DecksExport>(File.ReadAllText(file), CoreJson.Options);
    }

    private static DeckListItem ToListItem(DeckExport deck, CardCatalog catalog)
    {
        var code = ToDeckCode(deck, catalog);
        string? deckstring = null;
        if (code.Heroes.Count == 1)
        {
            try
            {
                deckstring = DeckstringCodec.Encode(code);
            }
            catch (InvalidDeckstringException)
            {
                deckstring = null;
            }
        }

        var cardClass = deck.CardClass?.ToUpperInvariant() ?? (code.Heroes.Count == 1 ? catalog.GetByDbfId(code.Heroes[0])?.CardClass : null);
        return new DeckListItem(deck.Origin + ":" + deck.ExternalId, deck.Origin, deck.ExternalId, deck.Name, cardClass, FormatName(deck, code), deck.Archived,
            code.TotalCards, deckstring, deck.LastEdited);
    }

    private static string? FormatName(DeckExport deck, DeckCode code) => code.Format switch
    {
        DeckFormat.Wild => "wild",
        DeckFormat.Standard => "standard",
        DeckFormat.Classic => "classic",
        DeckFormat.Twist => "twist",
        _ => deck.IsWild is { } wild ? (wild ? "wild" : "standard") : null,
    };

    /// <summary>Builds the deck model from an export: cards and sideboards as listed, the hero from the export or the class's basic hero.</summary>
    private static DeckCode ToDeckCode(DeckExport deck, CardCatalog catalog)
    {
        var cards = deck.Cards.Where(c => c.DbfId > 0).GroupBy(c => c.DbfId).Select(g => new DeckCard(g.Key, g.Sum(c => c.Count))).OrderBy(c => c.DbfId).ToList();
        var sideboards = deck.Sideboards
            .SelectMany(s => s.Cards.Where(c => c.DbfId > 0).Select(c => new SideboardCard(c.DbfId, c.Count, s.OwnerDbfId)))
            .OrderBy(s => s.OwnerDbfId).ThenBy(s => s.DbfId)
            .ToList();

        var heroes = new List<int>();
        if (deck.HeroDbfId is { } heroDbf && heroDbf > 0)
        {
            heroes.Add(heroDbf);
        }
        else if (deck.HeroCardId is not null && catalog.GetByCardId(deck.HeroCardId) is { } heroCard)
        {
            heroes.Add(heroCard.DbfId);
        }
        else if (BasicHero(deck.CardClass, catalog) is { } basic)
        {
            heroes.Add(basic);
        }

        var format = deck.FormatType switch
        {
            1 => DeckFormat.Wild,
            2 => DeckFormat.Standard,
            3 => DeckFormat.Classic,
            4 => DeckFormat.Twist,
            _ => deck.IsWild is { } wild ? (wild ? DeckFormat.Wild : DeckFormat.Standard) : DeckFormat.Standard,
        };

        return new DeckCode(format, heroes, cards, sideboards) { Name = deck.Name };
    }

    private static readonly Dictionary<string, string> BasicHeroCardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WARRIOR"] = "HERO_01",
        ["SHAMAN"] = "HERO_02",
        ["ROGUE"] = "HERO_03",
        ["PALADIN"] = "HERO_04",
        ["HUNTER"] = "HERO_05",
        ["DRUID"] = "HERO_06",
        ["WARLOCK"] = "HERO_07",
        ["MAGE"] = "HERO_08",
        ["PRIEST"] = "HERO_09",
        ["DEMONHUNTER"] = "HERO_10",
        ["DEATHKNIGHT"] = "HERO_11",
    };

    /// <summary>dbfId of the class's basic hero (HERO_01 … HERO_11), from the catalog; null when the class is unknown or the catalog lacks the card.</summary>
    public static int? BasicHero(string? cardClass, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(cardClass))
        {
            return null;
        }

        var key = cardClass.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (BasicHeroCardIds.TryGetValue(key, out var cardId) && catalog.GetByCardId(cardId) is { } hero)
        {
            return hero.DbfId;
        }

        // Fallback: any hero card of that class in the catalog.
        return catalog.All.FirstOrDefault(c => c.Type == "HERO" && string.Equals(c.CardClass, key, StringComparison.OrdinalIgnoreCase))?.DbfId;
    }
}
