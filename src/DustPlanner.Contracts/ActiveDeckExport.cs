namespace DustPlanner.Contracts;

/// <summary>
/// The deck selected in HDT's deck list (the one HDT tracks the current game with), written to
/// <c>active-deck.json</c> at game start, when the user picks another deck, on the menu transitions that already
/// export, and on manual export. Never keeps history on disk: the service stores the samples it sees.
/// </summary>
public sealed class ActiveDeckExport
{
    public ExportMeta Meta { get; set; } = new ExportMeta();

    /// <summary>Null when HDT has no active deck or the active deck is an Arena deck.</summary>
    public ActiveDeck? Deck { get; set; }
}

public sealed class ActiveDeck
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The deck as the game would serialise it (no comments), so the chat and the public page can hand it out.</summary>
    public string Deckstring { get; set; } = string.Empty;

    /// <summary>HDT's deck GUID (<c>D</c> format); matches <c>DeckExport.ExternalId</c> for <c>origin = hdt</c>.</summary>
    public string? HdtDeckId { get; set; }

    /// <summary>Upper-case class name (<c>PRIEST</c>), the same spelling the tracker stores in <c>game.player_class</c>.</summary>
    public string? CardClass { get; set; }

    /// <summary><c>standard</c>, <c>wild</c>, <c>classic</c> or <c>twist</c>; null when HearthDb reports an unknown format.</summary>
    public string? Format { get; set; }
}

/// <summary>Values of <see cref="ActiveDeck.Format"/>.</summary>
public static class DeckFormats
{
    public const string Standard = "standard";
    public const string Wild = "wild";
    public const string Classic = "classic";
    public const string Twist = "twist";
}
