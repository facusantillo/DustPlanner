using System;
using System.Security.Cryptography;
using System.Text;
using DustPlanner.Contracts;

namespace DustPlanner.HdtExporter.Logic;

/// <summary>What the gateway read about HDT's active deck, detached from HDT types.</summary>
public sealed class ActiveDeckInput
{
    public string Name { get; set; } = string.Empty;

    public string Deckstring { get; set; } = string.Empty;

    public string? HdtDeckId { get; set; }

    /// <summary>Class as HDT spells it (<c>Priest</c>); the builder upper-cases it.</summary>
    public string? CardClass { get; set; }

    /// <summary>HearthDb format name (<c>FT_STANDARD</c>) or already-normalised (<c>standard</c>); the builder normalises.</summary>
    public string? Format { get; set; }
}

/// <summary>Builds <see cref="ActiveDeckExport"/> and the content hash the throttle uses to skip unchanged exports.</summary>
public static class ActiveDeckExportBuilder
{
    /// <summary><paramref name="input"/> is null when HDT has no active deck (or it is an Arena deck): the export then carries <c>deck: null</c>.</summary>
    public static ActiveDeckExport Build(ActiveDeckInput? input, ExportMeta meta)
    {
        if (meta is null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        return new ActiveDeckExport
        {
            Meta = meta,
            Deck = input is null
                ? null
                : new ActiveDeck
                {
                    Name = input.Name ?? string.Empty,
                    Deckstring = input.Deckstring ?? string.Empty,
                    HdtDeckId = string.IsNullOrEmpty(input.HdtDeckId) ? null : input.HdtDeckId,
                    CardClass = NormaliseClass(input.CardClass),
                    Format = NormaliseFormat(input.Format),
                },
        };
    }

    /// <summary>Stable over everything the consumers read; <c>meta</c> (timestamps, trigger) is left out on purpose.</summary>
    public static string ContentHash(ActiveDeck? deck)
    {
        var text = deck is null
            ? "none"
            : string.Join("\n", deck.HdtDeckId ?? string.Empty, deck.Name, deck.Deckstring, deck.CardClass ?? string.Empty, deck.Format ?? string.Empty);
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }

    public static string? NormaliseClass(string? cardClass) =>
        string.IsNullOrWhiteSpace(cardClass) ? null : cardClass!.Trim().ToUpperInvariant();

    /// <summary><c>FT_STANDARD</c> / <c>Standard</c> / <c>standard</c> → <c>standard</c>; unknown values → null (conservative).</summary>
    public static string? NormaliseFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var name = format!.Trim();
        if (name.StartsWith("FT_", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(3);
        }

        switch (name.ToLowerInvariant())
        {
            case DeckFormats.Standard: return DeckFormats.Standard;
            case DeckFormats.Wild: return DeckFormats.Wild;
            case DeckFormats.Classic: return DeckFormats.Classic;
            case DeckFormats.Twist: return DeckFormats.Twist;
            default: return null;
        }
    }
}
