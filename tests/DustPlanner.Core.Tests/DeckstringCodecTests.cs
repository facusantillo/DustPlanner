using System.Text.Json;
using DustPlanner.Core.Deckstrings;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace DustPlanner.Core.Tests;

public class DeckstringCodecTests
{
    private static readonly Lazy<JsonDocument> Vectors = new(() => JsonDocument.Parse(TestData.Fixture("deckstrings.json")));

    public static IEnumerable<object[]> VectorNames() =>
        Vectors.Value.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new object[] { v.GetProperty("name").GetString()! });

    public static IEnumerable<object[]> InvalidNames() =>
        Vectors.Value.RootElement.GetProperty("invalid").EnumerateArray().Select(v => new object[] { v.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Decodes_python_hearthstone_vectors(string name)
    {
        var vector = Vectors.Value.RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

        var deck = DeckstringCodec.Decode(vector.GetProperty("deckstring").GetString()!);

        Assert.Equal(vector.GetProperty("format").GetInt32(), (int)deck.Format);
        Assert.Equal(vector.GetProperty("heroes").EnumerateArray().Select(h => h.GetInt32()).ToList(), deck.Heroes);
        Assert.Equal(
            vector.GetProperty("cards").EnumerateArray().Select(c => new DeckCard(c[0].GetInt32(), c[1].GetInt32())).ToList(),
            deck.Cards);
        Assert.Equal(
            vector.GetProperty("sideboards").EnumerateArray().Select(c => new SideboardCard(c[0].GetInt32(), c[1].GetInt32(), c[2].GetInt32())).ToList(),
            deck.Sideboards);

        var canonical = !vector.TryGetProperty("canonical", out var flag) || flag.GetBoolean();
        if (canonical)
        {
            Assert.Equal(vector.GetProperty("deckstring").GetString(), DeckstringCodec.Encode(deck));
        }
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Rejects_invalid_strings(string name)
    {
        var vector = Vectors.Value.RootElement.GetProperty("invalid").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
        var code = vector.GetProperty("deckstring").GetString()!;

        Assert.ThrowsAny<Exception>(() => DeckstringCodec.Decode(code));
        if (code.Length > 0)
        {
            Assert.Throws<InvalidDeckstringException>(() => DeckstringCodec.Decode(code));
        }
    }

    [Fact]
    public void Legacy_string_without_sideboard_marker_round_trips_to_a_marked_canonical_string()
    {
        var legacy = "AAEBAR8G+LEChwTmwgKhwgLZwgK7BQzquwKJwwKOwwKTwwK5tAK1A/4MqALsuwLrB86uAu0JAA==";
        var deck = DeckstringCodec.Decode(legacy);
        var reencoded = DeckstringCodec.Encode(deck);

        Assert.NotEqual(legacy, reencoded);
        Assert.Equal(deck, DeckstringCodec.Decode(reencoded) with { Name = null });
        Assert.Equal(30, deck.TotalCards);
    }

    [Fact]
    public void Block_parser_reads_name_and_ignores_comments()
    {
        var block = """
            ### Priest Control
            # Class: Priest
            # Format: Standard
            #
            # 2x (1) Holy Smite
            #
            AAECAQcABAECAwQAAA==
            #
            # To use this deck, copy it to your clipboard and create a new deck in Hearthstone
            """;

        var deck = DeckBlockParser.Parse(block);

        Assert.Equal("Priest Control", deck.Name);
        Assert.Equal(DeckFormat.Standard, deck.Format);
        Assert.Equal([7], deck.Heroes);
        Assert.Equal(4, deck.Cards.Count);
        Assert.Equal(8, deck.TotalCards);
    }

    [Fact]
    public void Block_parser_reads_many_decks_with_optional_names()
    {
        var text = "### A\nAAECAQcABAECAwQAAA==\n\nAAECAQcDCLMB2Q8AAAA=\n### C\n# comment\nAAECAQcABAECAwQAAA==\n";

        var decks = DeckBlockParser.ParseMany(text);

        Assert.Equal(3, decks.Count);
        Assert.Equal("A", decks[0].Name);
        Assert.Null(decks[1].Name);
        Assert.Equal("C", decks[2].Name);
    }

    [Fact]
    public void Encode_requires_exactly_one_hero()
    {
        var deck = new DeckCode(DeckFormat.Standard, [], [new DeckCard(1, 2)], []);
        Assert.Throws<InvalidDeckstringException>(() => DeckstringCodec.Encode(deck));
    }

    [Property(MaxTest = 200)]
    public Property Encode_then_decode_is_identity_for_canonical_decks(PositiveInt hero, NonEmptyArray<PositiveInt> ids, NonEmptyArray<PositiveInt> counts, byte formatByte)
    {
        var format = (DeckFormat)(formatByte % 5);
        var cards = ids.Get.Select(i => i.Get)
            .Distinct()
            .Select((id, index) => new DeckCard(id, 1 + counts.Get[index % counts.Get.Length].Get % 4))
            .OrderBy(c => c.DbfId)
            .ToList();
        var owner = cards[0].DbfId;
        var sideboards = cards.Skip(1).Take(3).Select(c => new SideboardCard(c.DbfId + 1_000_000, c.Count, owner)).OrderBy(s => s.DbfId).ToList();
        var deck = new DeckCode(format, [hero.Get], cards, sideboards);

        var decoded = DeckstringCodec.Decode(DeckstringCodec.Encode(deck));

        return (decoded == deck).ToProperty();
    }
}
