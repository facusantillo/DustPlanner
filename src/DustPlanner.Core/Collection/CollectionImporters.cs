using System.Globalization;
using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Json;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Collection;

public static class CollectionSources
{
    public const string Hdt = "hdt";
    public const string HsReplay = "hsreplay";
    public const string Csv = "csv";
}

/// <summary>Reads the JSON written by the DustPlanner HDT exporter (<see cref="CollectionExport"/>).</summary>
public static class HdtExportImporter
{
    public static CollectionSnapshot Import(string json, PremiumSlotMap slots)
    {
        ArgumentNullException.ThrowIfNull(json);
        var export = JsonSerializer.Deserialize<CollectionExport>(json, CoreJson.Options)
                     ?? throw new InvalidDataException("Empty collection export.");
        if (export.Meta.SchemaVersion > ExportSchema.Version)
        {
            throw new InvalidDataException($"collection.json uses schema {export.Meta.SchemaVersion}; this build understands up to {ExportSchema.Version}. Update DustPlanner.");
        }

        return new CollectionSnapshot(
            export.Meta.Account.AccountKey,
            export.Meta.ExportedAt,
            CollectionSources.Hdt,
            export.Dust,
            export.Cards.Select(p => CollectionSnapshot.FromHdtCounts(p.Key, p.Value, slots)),
            export.Gold,
            export.UnopenedPacks,
            export.Meta.Account.BattleTag,
            export.Meta.Account.Region);
    }

    public static CollectionSnapshot ImportFile(string path, PremiumSlotMap slots) => Import(File.ReadAllText(path), slots);
}

/// <summary>
/// Reads the JSON HSReplay.net serves for a synced collection (the same shape HDT uploads):
/// <c>{"collection": {"&lt;dbfId&gt;": [normal, golden, ...]}, "dust": N, ...}</c>. Arrays of 2, 3, 4 or 8 entries are accepted;
/// entries beyond index 1 go through the premium slot map like HDT's array.
/// </summary>
public static class HsReplayImporter
{
    public static CollectionSnapshot Import(string json, PremiumSlotMap slots, string accountKey = "hsreplay", DateTimeOffset? takenAt = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (!root.TryGetProperty("collection", out var collection) || collection.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("HSReplay JSON must contain a \"collection\" object keyed by dbfId.");
        }

        var dust = root.TryGetProperty("dust", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
        var cards = new List<OwnedCard>();
        foreach (var property in collection.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var dbfId) || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var counts = property.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0).ToArray();
            if (counts.Length > ExportSchema.PremiumSlotCount)
            {
                counts = counts.Take(ExportSchema.PremiumSlotCount).ToArray();
            }

            cards.Add(CollectionSnapshot.FromHdtCounts(dbfId, counts, slots));
        }

        return new CollectionSnapshot(accountKey, takenAt ?? DateTimeOffset.UtcNow, CollectionSources.HsReplay, dust, cards);
    }

    public static CollectionSnapshot ImportFile(string path, PremiumSlotMap slots, string accountKey = "hsreplay") =>
        Import(File.ReadAllText(path), slots, accountKey, File.GetLastWriteTimeUtc(path));
}

/// <summary>
/// Reads a hand-made CSV: header with <c>dbf_id</c> or <c>card_id</c> or <c>name</c>, then <c>normal</c>, <c>golden</c>
/// (optional <c>signature</c>, <c>diamond</c>). Names are resolved through the catalog; ambiguous names fail loudly.
/// </summary>
public static class CsvCollectionImporter
{
    public static CollectionSnapshot Import(string csv, CardCatalog catalog, int dust, string accountKey = "csv", DateTimeOffset? takenAt = null)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(catalog);
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        if (lines.Count == 0)
        {
            throw new InvalidDataException("CSV is empty.");
        }

        var header = Split(lines[0]).Select(h => h.Trim().ToLowerInvariant().Replace(" ", "_", StringComparison.Ordinal)).ToList();
        int Col(string name) => header.IndexOf(name);
        var dbfCol = Col("dbf_id");
        var idCol = Col("card_id");
        var nameCol = Col("name");
        if (dbfCol < 0 && idCol < 0 && nameCol < 0)
        {
            throw new InvalidDataException("CSV header needs a dbf_id, card_id or name column.");
        }

        var kindCols = new Dictionary<PremiumKind, int>
        {
            [PremiumKind.Normal] = Col("normal"),
            [PremiumKind.Golden] = Col("golden"),
            [PremiumKind.Signature] = Col("signature"),
            [PremiumKind.Diamond] = Col("diamond"),
        };
        if (kindCols[PremiumKind.Normal] < 0)
        {
            throw new InvalidDataException("CSV header needs a normal column.");
        }

        var cards = new Dictionary<int, Dictionary<PremiumKind, int>>();
        foreach (var line in lines.Skip(1))
        {
            var cells = Split(line);
            CardDef? card = null;
            if (dbfCol >= 0 && int.TryParse(Cell(cells, dbfCol), NumberStyles.None, CultureInfo.InvariantCulture, out var dbfId))
            {
                card = catalog.GetByDbfId(dbfId);
            }

            if (card is null && idCol >= 0)
            {
                card = catalog.GetByCardId(Cell(cells, idCol));
            }

            if (card is null && nameCol >= 0)
            {
                var matches = catalog.FindByName(Cell(cells, nameCol));
                card = matches.Count switch
                {
                    1 => matches[0],
                    0 => null,
                    _ => throw new InvalidDataException($"Card name '{Cell(cells, nameCol)}' is ambiguous ({matches.Count} matches); use dbf_id."),
                };
            }

            if (card is null)
            {
                throw new InvalidDataException($"Unknown card in CSV line: {line}");
            }

            var copies = cards.GetValueOrDefault(card.DbfId) ?? new Dictionary<PremiumKind, int>();
            foreach (var (kind, col) in kindCols)
            {
                if (col >= 0 && int.TryParse(Cell(cells, col), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                {
                    copies[kind] = copies.GetValueOrDefault(kind) + n;
                }
            }

            cards[card.DbfId] = copies;
        }

        return new CollectionSnapshot(accountKey, takenAt ?? DateTimeOffset.UtcNow, CollectionSources.Csv, dust,
            cards.Select(p => new OwnedCard(p.Key, p.Value, ToRaw(p.Value))));
    }

    private static int[] ToRaw(Dictionary<PremiumKind, int> copies) =>
        new[] { copies.GetValueOrDefault(PremiumKind.Normal), copies.GetValueOrDefault(PremiumKind.Golden), 0, 0, 0, 0, 0, 0 };

    private static string Cell(List<string> cells, int index) => index >= 0 && index < cells.Count ? cells[index].Trim() : string.Empty;

    private static List<string> Split(string line)
    {
        // Minimal CSV: commas or semicolons, optional double quotes.
        var separator = line.Contains(';') && !line.Contains(',') ? ';' : ',';
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == separator && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
