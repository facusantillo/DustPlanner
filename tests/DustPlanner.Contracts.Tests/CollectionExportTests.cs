using System.Text.Json;

namespace DustPlanner.Contracts.Tests;

public class CollectionExportTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Sample_file_deserializes_with_snake_case_names()
    {
        var export = JsonSerializer.Deserialize<CollectionExport>(Fixture("collection.sample.json"), ContractJson.Options);

        Assert.NotNull(export);
        Assert.Equal(1, export.Meta.SchemaVersion);
        Assert.Equal(ExportSchema.CollectionKind, export.Meta.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 13, 52, 10, TimeSpan.FromHours(2)), export.Meta.ExportedAt);
        Assert.Equal("EU", export.Meta.Account.Region);
        Assert.Equal("8589934592-123456789", export.Meta.Account.AccountKey);
        Assert.True(export.Meta.Capabilities.RawMirrorCollection);
        Assert.Equal(640, export.Dust);
        Assert.Null(export.Gold);
        Assert.Null(export.UnopenedPacks);
        Assert.Equal(4, export.Cards.Count);
        Assert.Equal(new[] { 1, 1, 0, 0, 0, 0, 0, 0 }, export.Cards[1002]);
    }

    [Fact]
    public void Round_trip_preserves_every_property_name()
    {
        var original = JsonDocument.Parse(Fixture("collection.sample.json"));
        var export = JsonSerializer.Deserialize<CollectionExport>(Fixture("collection.sample.json"), ContractJson.Options)!;
        var reserialized = JsonDocument.Parse(JsonSerializer.Serialize(export, ContractJson.Options));

        Assert.Equal(PropertyPaths(original.RootElement), PropertyPaths(reserialized.RootElement));
    }

    [Fact]
    public void Region_is_decoded_from_account_hi_like_hdt_does()
    {
        // (hi >> 32) & 0xFF: 1 = US, 2 = EU, 3 = ASIA, 5 = CHINA
        Assert.Equal("EU", AccountKeys.RegionFromHi(8589934592UL));
        Assert.Equal("US", AccountKeys.RegionFromHi(1UL << 32));
        Assert.Equal("ASIA", AccountKeys.RegionFromHi(3UL << 32));
        Assert.Equal("CHINA", AccountKeys.RegionFromHi(5UL << 32));
        Assert.Equal("UNKNOWN", AccountKeys.RegionFromHi(0));
    }

    [Fact]
    public void Account_key_round_trips()
    {
        var key = AccountKeys.Format(ulong.MaxValue, 7);
        Assert.True(AccountKeys.TryParse(key, out var hi, out var lo));
        Assert.Equal(ulong.MaxValue, hi);
        Assert.Equal(7UL, lo);
        Assert.False(AccountKeys.TryParse("nope", out _, out _));
        Assert.False(AccountKeys.TryParse("1-2-3", out _, out _));
        Assert.False(AccountKeys.TryParse(null, out _, out _));
    }

    [Fact]
    public void Decks_rank_and_diagnostics_round_trip()
    {
        var decks = new DecksExport
        {
            Meta = new ExportMeta { Kind = ExportSchema.DecksKind, ExportedAt = DateTimeOffset.UnixEpoch },
            Decks =
            {
                new DeckExport
                {
                    Origin = DeckOrigins.InGame,
                    ExternalId = "42",
                    Name = "Priest",
                    FormatType = 2,
                    DeckType = 1,
                    Cards = { new DeckCardExport { CardId = "CS1_130", DbfId = 1001, Count = 2, PremiumType = 0 } },
                    Sideboards = { new SideboardExport { OwnerCardId = "ETC_080", OwnerDbfId = 90749, Cards = { new DeckCardExport { DbfId = 5, Count = 1 } } } },
                },
            },
        };
        var rank = new RankExport { Formats = { [RankFormats.Standard] = new RankInfo { LeagueId = 2, StarLevel = 7, Stars = 3 } } };
        var diagnostics = new DiagnosticsExport
        {
            PremiumTypes = { new PremiumTypeStat { PremiumType = 2, DistinctCards = 1, TotalCopies = 1 } },
            Samples = { new DiagnosticCardSample { DbfId = 1003, Name = "X", RawCounts = new[] { 0, 0, 1, 0, 0, 0, 0, 0 }, MirrorEntries = { new RawMirrorEntry { PremiumType = 2, Count = 1 } } } },
            CoreSetPresent = true,
        };

        var decks2 = RoundTrip(decks);
        Assert.Equal("ETC_080", decks2.Decks[0].Sideboards[0].OwnerCardId);
        Assert.Equal(2, decks2.Decks[0].FormatType);

        var rank2 = RoundTrip(rank);
        Assert.Equal(7, rank2.Formats[RankFormats.Standard].StarLevel);

        var diag2 = RoundTrip(diagnostics);
        Assert.True(diag2.CoreSetPresent);
        Assert.Equal(2, diag2.Samples[0].MirrorEntries[0].PremiumType);

        var json = JsonSerializer.Serialize(decks, ContractJson.Options);
        Assert.Contains("\"external_id\"", json);
        Assert.Contains("\"owner_dbf_id\"", json);
        Assert.Contains("\"premium_type\"", json);
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ContractJson.Options), ContractJson.Options)!;

    private static List<string> PropertyPaths(JsonElement element, string prefix = "")
    {
        var paths = new List<string>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var path = prefix + "/" + property.Name;
                paths.Add(path);
                paths.AddRange(PropertyPaths(property.Value, path));
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }
}
