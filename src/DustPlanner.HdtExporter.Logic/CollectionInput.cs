using System.Collections.Generic;

namespace DustPlanner.HdtExporter.Logic;

/// <summary>One raw HearthMirror collection entry (one per card and premium type).</summary>
public sealed class MirrorCardEntry
{
    public MirrorCardEntry(string cardId, int dbfId, int premiumType, int count, int trialCount)
    {
        CardId = cardId;
        DbfId = dbfId;
        PremiumType = premiumType;
        Count = count;
        TrialCount = trialCount;
    }

    public string CardId { get; }

    /// <summary>0 when the card id could not be resolved to a dbfId.</summary>
    public int DbfId { get; }

    public int PremiumType { get; }

    public int Count { get; }

    public int TrialCount { get; }
}

/// <summary>Minimal card description the plugin resolves through HearthDb; the logic never touches HearthDb itself.</summary>
public sealed class CardInfo
{
    public CardInfo(int dbfId, string cardId, string name, string rarity, string set)
    {
        DbfId = dbfId;
        CardId = cardId;
        Name = name;
        Rarity = rarity;
        Set = set;
    }

    public int DbfId { get; }

    public string CardId { get; }

    public string Name { get; }

    /// <summary>HearthDb <c>Rarity</c> enum name (e.g. <c>LEGENDARY</c>).</summary>
    public string Rarity { get; }

    /// <summary>HearthDb <c>CardSet</c> enum name (e.g. <c>CORE</c>).</summary>
    public string Set { get; }
}

/// <summary>Everything the plugin read from HDT for a collection export, already detached from HDT types.</summary>
public sealed class CollectionInput
{
    /// <summary>dbfId → HDT 8-int count array.</summary>
    public IReadOnlyDictionary<int, int[]> HdtCards { get; set; } = new Dictionary<int, int[]>();

    public int Dust { get; set; }

    public int? Gold { get; set; }

    /// <summary>Raw HearthMirror entries, when the plugin read the full collection (diagnostics only).</summary>
    public IReadOnlyList<MirrorCardEntry>? RawCards { get; set; }
}
