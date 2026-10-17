namespace DustPlanner.Kick;

/// <summary>
/// Everything a chat answer can mention, detached from the host's state so the texts can be tested on their own.
/// Null members mean "not known yet" and make the command answer with its friendly fallback instead of an error.
/// </summary>
public sealed record ChatContext
{
    public static ChatContext Empty { get; } = new();

    /// <summary>The deck HDT is tracking with; null until the exporter samples one.</summary>
    public ChatDeck? Deck { get; init; }

    /// <summary>Public page of the deck when the streamer turned it on (stage K6); null otherwise.</summary>
    public string? DeckUrl { get; init; }

    public ChatRank? Rank { get; init; }

    public ChatRecord Today { get; init; } = new(0, 0, 0);

    public ChatRecord Session { get; init; } = new(0, 0, 0);

    /// <summary>Positive for a winning streak, negative for a losing one, 0 when nothing is going on.</summary>
    public int Streak { get; init; }

    public ChatLastGame? LastGame { get; init; }

    /// <summary>Arcane dust balance; null until the collection export exists.</summary>
    public int? Dust { get; init; }
}

public sealed record ChatDeck(string Name, string Deckstring, string? CardClass, string? Format);

/// <summary>Ladder standing as the exporter reports it: league 1..5 (Bronze..Diamond), star level 1..10, Legend rank when &gt; 0.</summary>
public sealed record ChatRank(int LeagueId, int StarLevel, int Stars, int LegendRank, string Format);

public sealed record ChatRecord(int Wins, int Losses, int Ties)
{
    public int Games => Wins + Losses + Ties;

    /// <summary>Wins over decided games; null when nothing was decided yet.</summary>
    public double? WinRate => Wins + Losses == 0 ? null : (double)Wins / (Wins + Losses);
}

/// <summary><paramref name="Result"/> is <c>win</c>, <c>loss</c> or <c>tie</c>; classes are upper-case ids (<c>MAGE</c>).</summary>
public sealed record ChatLastGame(string Result, string? PlayerClass, string? OpponentClass, int Turns);
