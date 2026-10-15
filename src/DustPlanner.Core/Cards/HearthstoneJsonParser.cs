using System.Text.Json;

namespace DustPlanner.Core.Cards;

/// <summary>Parses HearthstoneJSON <c>cards.collectible.json</c> (or <c>cards.json</c>) into <see cref="CardDef"/>s.</summary>
public static class HearthstoneJsonParser
{
    public static CardCatalog Parse(Stream json, string build, string locale)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return new CardCatalog(ParseCards(document.RootElement), build, locale);
    }

    public static CardCatalog Parse(string json, string build, string locale)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return new CardCatalog(ParseCards(document.RootElement), build, locale);
    }

    private static List<CardDef> ParseCards(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("HearthstoneJSON card file must be a JSON array.");
        }

        var cards = new List<CardDef>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            if (!element.TryGetProperty("dbfId", out var dbf) || dbf.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var collectible = element.TryGetProperty("collectible", out var col) && col.ValueKind == JsonValueKind.True;
            if (!collectible)
            {
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

        return cards;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
