using System.Globalization;
using System.Text.Json;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Json;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;

namespace DustPlanner.App;

/// <summary>A card the user can block or search for.</summary>
public sealed record CardSummaryJson(int DbfId, string CardId, string Name, string Rarity, string CardClass, string Set, string Type);

/// <summary>One per-card craftability override of the user.</summary>
public sealed record CardOverrideJson(int DbfId, string? Name, string? Set, string Kind, bool Uncraftable, string? ShippedRule);

/// <summary>What <c>meta.txt</c> holds: the text, and how the planner reads it.</summary>
public sealed record MetaJson(string Text, int DeckCount, IReadOnlyList<string> Warnings);

public sealed partial class AppServices
{
    // ----- prefs.json -----------------------------------------------------------------------------------------------

    public string PrefsPath => Path.Combine(Paths.Config, UserPrefs.FileName);

    /// <summary>The preferences as the file holds them (every field, defaults filled in) so a form can edit them.</summary>
    public UserPrefs ReadPrefs() => LoadPrefs(null);

    /// <summary>Validates and writes <c>prefs.json</c>; returns the stored preferences.</summary>
    public UserPrefs WritePrefs(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        UserPrefs prefs;
        try
        {
            prefs = JsonSerializer.Deserialize<UserPrefs>(json, CoreJson.Options) ?? throw new AppException("prefs.json cannot be empty.");
        }
        catch (JsonException ex)
        {
            throw new AppException("prefs.json is not valid: " + ex.Message);
        }

        if (!string.Equals(prefs.Format, "standard", StringComparison.OrdinalIgnoreCase) && !string.Equals(prefs.Format, "wild", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("format must be 'standard' or 'wild'.");
        }

        prefs.Format = prefs.Format.ToLowerInvariant();
        prefs.ClassesPlayed = prefs.ClassesPlayed.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        prefs.Chat ??= new ChatPrefs();
        prefs.Kick ??= new KickPrefs();
        var blockErrors = prefs.Chat.Validate().Concat(prefs.Kick.Validate()).ToList();
        if (blockErrors.Count > 0)
        {
            throw new AppException(string.Join(" ", blockErrors));
        }

        Directory.CreateDirectory(Paths.Config);
        File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs, CoreJson.Options));
        return prefs;
    }

    // ----- meta.txt -------------------------------------------------------------------------------------------------

    public string MetaPath => Path.Combine(Paths.Config, MetaSignal.FileName);

    public MetaJson ReadMeta(CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var text = File.Exists(MetaPath) ? File.ReadAllText(MetaPath) : string.Empty;
        return DescribeMeta(text, catalog);
    }

    /// <summary>
    /// Writes <c>meta.txt</c> (deck codes, one per line, <c>### name</c> labels) and reports how many decks it yields.
    /// A line that is not a deck code is refused (the planner would otherwise fail on every run).
    /// </summary>
    public MetaJson WriteMeta(string text, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(catalog);
        var described = DescribeMeta(text, catalog);
        if (described.Warnings.Any(w => w.StartsWith("Line ", StringComparison.Ordinal)))
        {
            throw new AppException(string.Join(" ", described.Warnings));
        }

        Directory.CreateDirectory(Paths.Config);
        File.WriteAllText(MetaPath, text);
        return described;
    }

    private static MetaJson DescribeMeta(string text, CardCatalog catalog)
    {
        var warnings = new List<string>();
        var decks = 0;
        try
        {
            decks = MetaSignal.FromText(text, catalog).DeckCount;
        }
        catch (InvalidDeckstringException ex)
        {
            warnings.Add(BadMetaLine(text, ex));
        }

        if (decks == 0 && !string.IsNullOrWhiteSpace(text) && warnings.Count == 0)
        {
            warnings.Add("No deck code recognised: paste one code per line (AAECA…), optionally preceded by a '### name' line.");
        }

        return new MetaJson(text, decks, warnings);
    }

    /// <summary>"Line 3 is not a valid deck code (…): …" — the first line that fails to decode on its own.</summary>
    private static string BadMetaLine(string text, InvalidDeckstringException error)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            try
            {
                DeckstringCodec.Decode(line);
            }
            catch (InvalidDeckstringException)
            {
                var shown = line.Length > 40 ? line[..40] + "…" : line;
                return $"Line {i + 1} is not a valid deck code ({shown}): {error.Message}";
            }
        }

        return "A line is not a valid deck code: " + error.Message;
    }

    // ----- per-card craftability overrides ----------------------------------------------------------------------------

    /// <summary>
    /// Resolves a card argument: a dbf id, an exact name, or a unique part of the name (case-insensitive). Hero skins,
    /// hidden Core variants and cards of sets that are uncraftable anyway are not candidates when another match exists.
    /// </summary>
    public CardDef ResolveCard(string cardArgument, CardCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardArgument);
        ArgumentNullException.ThrowIfNull(catalog);
        if (int.TryParse(cardArgument.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dbfId))
        {
            return catalog.GetByDbfId(dbfId) ?? throw new AppException($"No card with dbf id {dbfId} in catalog build {catalog.Build}.");
        }

        var craftability = Rules.Craftability;
        List<CardDef> Prefer(List<CardDef> cards)
        {
            var craftableSets = cards.Where(c => !craftability.IsSetNonCraftable(c.Set)).ToList();
            return craftableSets.Count > 0 ? craftableSets : cards;
        }

        var matches = Prefer(catalog.FindByName(cardArgument.Trim()).Where(IsBlockable).ToList());
        if (matches.Count == 0)
        {
            var partial = Prefer(SearchCards(cardArgument, catalog, 50).ToList());
            var names = partial.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 1)
            {
                matches = partial;
            }
            else if (names.Count > 1)
            {
                throw new AppException($"No card named '{cardArgument}'; did you mean: " + string.Join(", ", partial.Take(12).Select(m => $"{m.Name} [{m.Set}] = {m.DbfId}")) + (partial.Count > 12 ? ", …" : "") + "? Pass the full name or the dbf id.");
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AppException($"No collectible card named '{cardArgument}' in catalog build {catalog.Build} (names are case-insensitive; a unique part of the name is enough)."),
            _ => throw new AppException($"'{cardArgument}' matches several cards; pass the dbf id instead: " + string.Join(", ", matches.Select(m => $"{m.Name} [{m.Set}] = {m.DbfId}"))),
        };
    }

    /// <summary>Collectible cards whose name contains the text, best matches first (name starts with the text, then shorter names).</summary>
    public static IEnumerable<CardDef> SearchCards(string text, CardCatalog catalog, int limit)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var query = (text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return Array.Empty<CardDef>();
        }

        return catalog.All
            .Where(c => IsBlockable(c) && c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Name.Length)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.DbfId)
            .Take(Math.Clamp(limit, 1, 200));
    }

    /// <summary>Records the game's verdict for a copy (<paramref name="uncraftable"/> true: never proposed; false: craftable although the data says otherwise).</summary>
    public CardOverrideJson SetCardOverride(CardDef card, PremiumKind kind, bool uncraftable)
    {
        ArgumentNullException.ThrowIfNull(card);
        Craftability.WriteOverride(Paths.DataOverrides, card.DbfId, kind, uncraftable);
        _rules = null; // the merged rules change
        return new CardOverrideJson(card.DbfId, card.Name, card.Set, kind.ToString().ToLowerInvariant(), uncraftable, null);
    }

    /// <summary>Removes the user's override for a copy; the shipped rule applies again.</summary>
    public bool ClearCardOverride(int dbfId, PremiumKind kind)
    {
        var removed = Craftability.ClearOverride(Paths.DataOverrides, dbfId, kind);
        _rules = null;
        return removed;
    }

    /// <summary>The user's overrides with card names and what the shipped data would have said.</summary>
    public IReadOnlyList<CardOverrideJson> ListCardOverrides(CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var shipped = Craftability.Load(null);
        var result = new List<CardOverrideJson>();
        foreach (var (key, kind, uncraftable) in Craftability.ReadOverrides(Paths.DataOverrides))
        {
            var card = int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var dbf) ? catalog.GetByDbfId(dbf) : catalog.GetByCardId(key);
            var rule = card is null ? null : shipped.Explain(card, kind).Reason ?? "craftable";
            result.Add(new CardOverrideJson(card?.DbfId ?? dbf, card?.Name, card?.Set, kind.ToString().ToLowerInvariant(), uncraftable, rule));
        }

        return result.OrderBy(o => o.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Kind, StringComparer.Ordinal).ToList();
    }

    private static bool IsBlockable(CardDef card) => card.Set != "HERO_SKINS" && card.Set != "CORE_HIDDEN";
}
