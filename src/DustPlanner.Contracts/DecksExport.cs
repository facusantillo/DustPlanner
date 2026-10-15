using System;
using System.Collections.Generic;

namespace DustPlanner.Contracts;

/// <summary>Decks known to HDT: the ones stored in the game client and the ones HDT keeps locally.</summary>
public sealed class DecksExport
{
    public ExportMeta Meta { get; set; } = new ExportMeta();

    public List<DeckExport> Decks { get; set; } = new List<DeckExport>();
}

public sealed class DeckExport
{
    /// <summary><see cref="DeckOrigins.InGame"/> or <see cref="DeckOrigins.Hdt"/>.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>In-game deck id, or HDT deck GUID, as a string.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? HeroCardId { get; set; }

    public int? HeroDbfId { get; set; }

    /// <summary>Class name as HDT reports it (e.g. <c>Priest</c>), when known.</summary>
    public string? CardClass { get; set; }

    /// <summary>HearthMirror <c>Deck.FormatType</c> (1 = Wild, 2 = Standard, 3 = Classic, 4 = Twist). Null for HDT decks.</summary>
    public int? FormatType { get; set; }

    /// <summary>HearthMirror <c>Deck.Type</c> (constructed decks are type 1; Tavern Brawl decks are type 6). Null for HDT decks.</summary>
    public int? DeckType { get; set; }

    /// <summary>True for HDT decks the user archived; in-game decks are never archived.</summary>
    public bool Archived { get; set; }

    /// <summary>True when HDT flags the deck as Wild (HDT decks only).</summary>
    public bool? IsWild { get; set; }

    public DateTimeOffset? LastEdited { get; set; }

    public List<DeckCardExport> Cards { get; set; } = new List<DeckCardExport>();

    public List<SideboardExport> Sideboards { get; set; } = new List<SideboardExport>();
}

public sealed class DeckCardExport
{
    public string? CardId { get; set; }

    public int DbfId { get; set; }

    public int Count { get; set; }

    /// <summary>PremiumType of the copy in the deck (0 regular, 1 golden, 2/3 unconfirmed). Null when the source does not say.</summary>
    public int? PremiumType { get; set; }
}

public sealed class SideboardExport
{
    public string? OwnerCardId { get; set; }

    public int OwnerDbfId { get; set; }

    public List<DeckCardExport> Cards { get; set; } = new List<DeckCardExport>();
}

public static class DeckOrigins
{
    /// <summary>Deck read from the game client through HearthMirror.</summary>
    public const string InGame = "ingame";

    /// <summary>Deck stored by HDT in <c>PlayerDecks.xml</c>.</summary>
    public const string Hdt = "hdt";
}
