namespace DustPlanner.Core.Deckstrings;

/// <summary>
/// Accepts what the game copies to the clipboard: comment lines starting with <c>#</c>, a <c>### Name</c> line, and
/// the base64 code itself. Also accepts a bare code.
/// </summary>
public static class DeckBlockParser
{
    public static DeckCode Parse(string block)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(block);
        string? name = null;
        string? code = null;
        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("###", StringComparison.Ordinal))
            {
                name ??= line[3..].Trim();
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            code ??= line;
        }

        if (code is null)
        {
            throw new InvalidDeckstringException("No deck code found in the pasted text.");
        }

        return DeckstringCodec.Decode(code) with { Name = name };
    }

    /// <summary>Parses every code in a text with one deck per block (used for meta.txt and multi-deck files).</summary>
    public static IReadOnlyList<DeckCode> ParseMany(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var decks = new List<DeckCode>();
        string? name = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("###", StringComparison.Ordinal))
            {
                name = line[3..].Trim();
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            decks.Add(DeckstringCodec.Decode(line) with { Name = name });
            name = null;
        }

        return decks;
    }
}
