using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DustPlanner.Contracts;

namespace DustPlanner.HdtExporter.Logic;

public static class CollectionExportBuilder
{
    public static CollectionExport Build(CollectionInput input, ExportMeta meta)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (meta is null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        var cards = new Dictionary<int, int[]>(input.HdtCards.Count);
        foreach (var pair in input.HdtCards)
        {
            cards[pair.Key] = NormalizeCounts(pair.Value);
        }

        meta.Kind = ExportSchema.CollectionKind;
        return new CollectionExport
        {
            Meta = meta,
            Dust = input.Dust,
            Gold = input.Gold,
            UnopenedPacks = null,
            Cards = cards,
            ContentHash = ComputeContentHash(input.Dust, cards),
        };
    }

    /// <summary>
    /// Pads or truncates to <see cref="ExportSchema.CountArrayLength"/> so a change in HDT's array shape
    /// degrades to zeros instead of breaking the contract.
    /// </summary>
    public static int[] NormalizeCounts(int[]? raw)
    {
        var result = new int[ExportSchema.CountArrayLength];
        if (raw is null)
        {
            return result;
        }

        Array.Copy(raw, result, Math.Min(raw.Length, result.Length));
        return result;
    }

    /// <summary>
    /// Deterministic hash of the collection content: <c>dust|dbfId:c0,c1,...;dbfId:...</c> with dbfIds ascending.
    /// Entries whose counts are all zero are ignored so an empty row does not change the hash.
    /// </summary>
    public static string ComputeContentHash(int dust, IReadOnlyDictionary<int, int[]> cards)
    {
        if (cards is null)
        {
            throw new ArgumentNullException(nameof(cards));
        }

        var sb = new StringBuilder();
        sb.Append(dust.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (var pair in cards.OrderBy(p => p.Key))
        {
            var counts = NormalizeCounts(pair.Value);
            if (counts.All(c => c == 0))
            {
                continue;
            }

            sb.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(':');
            sb.Append(string.Join(",", counts.Select(c => c.ToString(CultureInfo.InvariantCulture))));
            sb.Append(';');
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }
}
