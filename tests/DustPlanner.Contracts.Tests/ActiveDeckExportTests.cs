using System.Text.Json;

namespace DustPlanner.Contracts.Tests;

public class ActiveDeckExportTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Sample_file_deserializes_with_the_shared_envelope()
    {
        var export = JsonSerializer.Deserialize<ActiveDeckExport>(Fixture("active-deck.sample.json"), ContractJson.Options);

        Assert.NotNull(export);
        Assert.Equal(ExportSchema.ActiveDeckKind, export.Meta.Kind);
        // The writer names files "<kind>.json": the hyphenated kind is what makes the file active-deck.json.
        Assert.Equal(ExportSchema.ActiveDeckFileName, ExportSchema.ActiveDeckKind + ".json");
        Assert.Equal("game_start", export.Meta.Trigger);
        Assert.Equal("8589934592-123456789", export.Meta.Account.AccountKey);
        Assert.True(export.Meta.Capabilities.ActiveDeck);
        Assert.NotNull(export.Deck);
        Assert.Equal("Priest Control", export.Deck.Name);
        Assert.Equal("6f0c2a1e-5b7d-4e3a-9c21-0f8d6a4b2c11", export.Deck.HdtDeckId);
        Assert.Equal("PRIEST", export.Deck.CardClass);
        Assert.Equal(DeckFormats.Standard, export.Deck.Format);
    }

    [Fact]
    public void Round_trip_preserves_every_property_name()
    {
        var original = JsonDocument.Parse(Fixture("active-deck.sample.json"));
        var export = JsonSerializer.Deserialize<ActiveDeckExport>(Fixture("active-deck.sample.json"), ContractJson.Options)!;
        var reserialized = JsonDocument.Parse(JsonSerializer.Serialize(export, ContractJson.Options));

        Assert.Equal(PropertyPaths(original.RootElement), PropertyPaths(reserialized.RootElement));
    }

    [Fact]
    public void Deck_is_written_as_an_explicit_null_when_there_is_no_active_deck()
    {
        var export = new ActiveDeckExport { Meta = new ExportMeta { Kind = ExportSchema.ActiveDeckKind, Trigger = "mode_changed:HUB" }, Deck = null };
        var json = JsonSerializer.Serialize(export, ContractJson.Options);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("deck").ValueKind);
        var back = JsonSerializer.Deserialize<ActiveDeckExport>(json, ContractJson.Options);
        Assert.NotNull(back);
        Assert.Null(back.Deck);
    }

    [Fact]
    public void Older_exports_without_the_active_deck_capability_still_load()
    {
        // The capability was added later; a file written by exporter 1.0.0 has no "active_deck" key and must read as false.
        var json = System.Text.RegularExpressions.Regex.Replace(Fixture("collection.sample.json"), @",\s*""active_deck"":\s*true", string.Empty);
        Assert.DoesNotContain("active_deck", json, StringComparison.Ordinal);
        var export = JsonSerializer.Deserialize<CollectionExport>(json, ContractJson.Options);
        Assert.NotNull(export);
        Assert.False(export.Meta.Capabilities.ActiveDeck);
    }

    private static SortedSet<string> PropertyPaths(JsonElement element, string prefix = "")
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return paths;
        }

        foreach (var property in element.EnumerateObject())
        {
            var path = prefix + property.Name;
            paths.Add(path);
            paths.UnionWith(PropertyPaths(property.Value, path + "."));
        }

        return paths;
    }
}
