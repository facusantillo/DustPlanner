namespace DustPlanner.Core.Deckstrings;

/// <summary>Hearthstone <c>FormatType</c> values carried by a deckstring.</summary>
public enum DeckFormat
{
    Unknown = 0,
    Wild = 1,
    Standard = 2,
    Classic = 3,
    Twist = 4,
}

/// <summary>A main-deck entry: dbfId and copies.</summary>
public readonly record struct DeckCard(int DbfId, int Count);

/// <summary>A sideboard entry: dbfId, copies and the dbfId of the card that owns the sideboard (E.T.C., Zilliax, ...).</summary>
public readonly record struct SideboardCard(int DbfId, int Count, int OwnerDbfId);

/// <summary>Decoded deckstring. Lists are canonical: sorted by dbfId (sideboards by owner, then dbfId).</summary>
public sealed record DeckCode(
    DeckFormat Format,
    IReadOnlyList<int> Heroes,
    IReadOnlyList<DeckCard> Cards,
    IReadOnlyList<SideboardCard> Sideboards)
{
    /// <summary>Deck name parsed from a <c>### Name</c> line, when the code was pasted as a block.</summary>
    public string? Name { get; init; }

    /// <summary>Value equality over the sequences (records compare list references by default).</summary>
    public bool Equals(DeckCode? other) =>
        other is not null
        && Format == other.Format
        && Name == other.Name
        && Heroes.SequenceEqual(other.Heroes)
        && Cards.SequenceEqual(other.Cards)
        && Sideboards.SequenceEqual(other.Sideboards);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Format);
        hash.Add(Name);
        foreach (var hero in Heroes)
        {
            hash.Add(hero);
        }

        foreach (var card in Cards)
        {
            hash.Add(card);
        }

        foreach (var side in Sideboards)
        {
            hash.Add(side);
        }

        return hash.ToHashCode();
    }

    public int TotalCards => Cards.Sum(c => c.Count);

    public int TotalSideboardCards => Sideboards.Sum(c => c.Count);

    public DeckCard? Find(int dbfId)
    {
        foreach (var card in Cards)
        {
            if (card.DbfId == dbfId)
            {
                return card;
            }
        }

        return null;
    }
}
