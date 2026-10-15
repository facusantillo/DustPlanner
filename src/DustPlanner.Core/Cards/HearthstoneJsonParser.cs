using System.Text.Json;

namespace DustPlanner.Core.Cards;

/// <summary>Parses HearthstoneJSON <c>cards.json</c> (or <c>cards.collectible.json</c>, then without companions) into <see cref="CardDef"/>s.</summary>
public static class HearthstoneJsonParser
{
    public static CardCatalog Parse(Stream json, string build, string locale)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var (cards, companions) = ParseCards(document.RootElement);
        return new CardCatalog(cards, companions, build, locale);
    }

    public static CardCatalog Parse(string json, string build, string locale)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var (cards, companions) = ParseCards(document.RootElement);
        return new CardCatalog(cards, companions, build, locale);
    }

    private static (List<CardDef> Cards, List<CompanionCard> Companions) ParseCards(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("HearthstoneJSON card file must be a JSON array.");
        }

        var cards = new List<CardDef>();
        var tokens = new List<(int DbfId, string CardId, string Name, string Type)>();
        foreach (var element in root.EnumerateArray())
        {
            if (!element.TryGetProperty("dbfId", out var dbf) || dbf.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var collectible = element.TryGetProperty("collectible", out var col) && col.ValueKind == JsonValueKind.True;
            if (!collectible)
            {
                // Only present in cards.json (cards.collectible.json has no tokens): kept when the id points at a collectible parent.
                var tokenId = GetString(element, "id") ?? string.Empty;
                if (ParentCardId(tokenId) is not null)
                {
                    tokens.Add((dbf.GetInt32(), tokenId, GetString(element, "name") ?? string.Empty, GetString(element, "type") ?? string.Empty));
                }

                continue;
            }

            var cardClass = GetString(element, "cardClass") ?? CardClasses.Neutral;
            var classes = element.TryGetProperty("classes", out var cls) && cls.ValueKind == JsonValueKind.Array
                ? cls.EnumerateArray().Select(c => c.GetString()).Where(c => c is not null).Cast<string>().ToList()
                : new List<string> { cardClass };
            if (!classes.Contains(cardClass, StringComparer.Ordinal))
            {
                classes.Add(cardClass);
            }

            cards.Add(new CardDef
            {
                DbfId = dbf.GetInt32(),
                CardId = GetString(element, "id") ?? string.Empty,
                Name = GetString(element, "name") ?? string.Empty,
                Set = GetString(element, "set") ?? string.Empty,
                Rarity = GetString(element, "rarity") ?? string.Empty,
                CardClass = cardClass,
                Classes = classes,
                Type = GetString(element, "type") ?? string.Empty,
                Cost = element.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetInt32() : 0,
                Collectible = true,
            });
        }

        var byCardId = cards.ToDictionary(c => c.CardId, c => c.DbfId, StringComparer.Ordinal);
        var companions = new List<CompanionCard>();
        foreach (var (dbfId, cardId, name, type) in tokens)
        {
            var parentId = ParentCardId(cardId);
            if (parentId is not null && byCardId.TryGetValue(parentId, out var parentDbfId))
            {
                companions.Add(new CompanionCard(dbfId, cardId, name, type, parentDbfId));
            }
        }

        return (cards, companions);
    }

    /// <summary><c>TIME_890t2</c> → <c>TIME_890</c>; null when the id does not look like a token.</summary>
    internal static string? ParentCardId(string cardId)
    {
        var t = cardId.LastIndexOf('t');
        if (t <= 0)
        {
            return null;
        }

        for (var i = t + 1; i < cardId.Length; i++)
        {
            if (!char.IsAsciiDigit(cardId[i]))
            {
                return null;
            }
        }

        return cardId[..t];
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
