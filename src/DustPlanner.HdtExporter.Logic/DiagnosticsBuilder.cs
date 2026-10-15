using System;
using System.Collections.Generic;
using System.Linq;
using DustPlanner.Contracts;

namespace DustPlanner.HdtExporter.Logic;

/// <summary>Selects the cards whose raw data answers the open verification questions (premium index mapping, Core set).</summary>
public static class DiagnosticsBuilder
{
    public const int DefaultMaxSamples = 40;

    public static DiagnosticsExport Build(
        CollectionInput input,
        Func<int, CardInfo?> resolveCard,
        ExportMeta meta,
        int maxSamples = DefaultMaxSamples)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (resolveCard is null)
        {
            throw new ArgumentNullException(nameof(resolveCard));
        }

        if (meta is null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        meta.Kind = ExportSchema.DiagnosticsKind;
        var result = new DiagnosticsExport { Meta = meta };

        var raw = input.RawCards ?? Array.Empty<MirrorCardEntry>();
        if (raw.Count == 0)
        {
            result.Notes.Add("Raw HearthMirror collection was not available; premium-type statistics are derived from HDT's count array only.");
        }

        // 1. Premium-type statistics from the raw entries (preferred) or from the HDT array (fallback).
        if (raw.Count > 0)
        {
            foreach (var group in raw.GroupBy(e => e.PremiumType).OrderBy(g => g.Key))
            {
                result.PremiumTypes.Add(new PremiumTypeStat
                {
                    PremiumType = group.Key,
                    DistinctCards = group.Select(e => e.CardId).Distinct(StringComparer.Ordinal).Count(),
                    TotalCopies = group.Sum(e => e.Count),
                    TrialCopies = group.Sum(e => e.TrialCount),
                });
            }
        }
        else
        {
            for (var slot = 0; slot < ExportSchema.PremiumSlotCount; slot++)
            {
                var s = slot;
                var withCopies = input.HdtCards.Where(p => Slot(p.Value, s) > 0).ToList();
                result.PremiumTypes.Add(new PremiumTypeStat
                {
                    PremiumType = s,
                    DistinctCards = withCopies.Count,
                    TotalCopies = withCopies.Sum(p => Slot(p.Value, s)),
                    TrialCopies = input.HdtCards.Sum(p => Slot(p.Value, s + ExportSchema.PremiumSlotCount)),
                });
            }
        }

        // 2. Samples: every card with copies in an unconfirmed slot first, then a few golden-only and regular references.
        var unconfirmed = input.HdtCards
            .Where(p => Slot(p.Value, ExportSchema.PremiumUnknownA) > 0 || Slot(p.Value, ExportSchema.PremiumUnknownB) > 0)
            .OrderBy(p => p.Key)
            .Select(p => p.Key)
            .ToList();
        var golden = input.HdtCards
            .Where(p => Slot(p.Value, ExportSchema.PremiumGolden) > 0 && !unconfirmed.Contains(p.Key))
            .OrderBy(p => p.Key)
            .Select(p => p.Key)
            .Take(5)
            .ToList();
        var regular = input.HdtCards
            .Where(p => Slot(p.Value, ExportSchema.PremiumNormal) > 0 && !unconfirmed.Contains(p.Key) && !golden.Contains(p.Key))
            .OrderBy(p => p.Key)
            .Select(p => p.Key)
            .Take(3)
            .ToList();

        var rawByDbf = raw.Where(e => e.DbfId != 0).ToLookup(e => e.DbfId);
        foreach (var dbfId in unconfirmed.Take(maxSamples).Concat(golden).Concat(regular))
        {
            var info = resolveCard(dbfId);
            result.Samples.Add(new DiagnosticCardSample
            {
                DbfId = dbfId,
                CardId = info?.CardId,
                Name = info?.Name,
                Rarity = info?.Rarity,
                Set = info?.Set,
                RawCounts = CollectionExportBuilder.NormalizeCounts(input.HdtCards[dbfId]),
                MirrorEntries = rawByDbf[dbfId]
                    .OrderBy(e => e.PremiumType)
                    .Select(e => new RawMirrorEntry { PremiumType = e.PremiumType, Count = e.Count, TrialCount = e.TrialCount })
                    .ToList(),
            });
        }

        if (unconfirmed.Count > maxSamples)
        {
            result.Notes.Add($"{unconfirmed.Count} cards have copies in premium slots 2/3; only the first {maxSamples} are listed.");
        }

        if (unconfirmed.Count == 0)
        {
            result.Notes.Add("No card has copies in premium slots 2 or 3. The mapping of those slots cannot be confirmed from this collection.");
        }

        // 3. Core set presence.
        var coreNames = new List<string>();
        var coreCount = 0;
        foreach (var dbfId in input.HdtCards.Keys.OrderBy(k => k))
        {
            var info = resolveCard(dbfId);
            if (info is null || !string.Equals(info.Set, "CORE", StringComparison.Ordinal))
            {
                continue;
            }

            coreCount++;
            if (coreNames.Count < 10)
            {
                coreNames.Add(info.Name);
            }
        }

        result.CoreSetPresent = coreCount > 0;
        result.CoreSetDistinctCards = coreCount;
        result.CoreSetSampleNames = coreNames;
        result.Notes.Add(coreCount > 0
            ? $"{coreCount} CORE-set cards appear in the exported collection."
            : "No CORE-set card appears in the exported collection.");

        return result;
    }

    private static int Slot(int[]? counts, int index) =>
        counts is not null && index < counts.Length ? counts[index] : 0;
}
