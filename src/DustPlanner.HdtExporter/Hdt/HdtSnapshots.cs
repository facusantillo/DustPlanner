using System;
using System.Collections.Generic;
using DustPlanner.Contracts;
using DustPlanner.HdtExporter.Logic;

namespace DustPlanner.HdtExporter.Hdt;

/// <summary>HDT's processed collection, detached from HDT types.</summary>
internal sealed class HdtCollectionSnapshot
{
    public ulong AccountHi { get; set; }

    public ulong AccountLo { get; set; }

    public string? BattleTag { get; set; }

    public int Dust { get; set; }

    public IReadOnlyDictionary<int, int[]> Cards { get; set; } = new Dictionary<int, int[]>();
}

/// <summary>Raw HearthMirror collection (one entry per card and premium type), used for diagnostics and gold.</summary>
internal sealed class RawCollectionSnapshot
{
    public int Dust { get; set; }

    public int? Gold { get; set; }

    public IReadOnlyList<MirrorCardEntry> Cards { get; set; } = Array.Empty<MirrorCardEntry>();
}

internal sealed class AccountSnapshot
{
    public ulong Hi { get; set; }

    public ulong Lo { get; set; }

    public string? BattleTag { get; set; }
}

/// <summary>
/// The subset of HDT the exporter needs. Every member returns <c>null</c> (and flips the matching
/// capability flag) when the underlying API is missing in the HDT build the plugin was loaded into.
/// </summary>
internal interface IHdtGateway
{
    ExporterCapabilities Capabilities { get; }

    Version? HdtVersion { get; }

    Version? HearthMirrorVersion { get; }

    Version? HearthDbVersion { get; }

    bool IsGameRunning { get; }

    /// <summary>HDT's cached collection for the current account, without forcing a memory read.</summary>
    HdtCollectionSnapshot? GetCachedCollection();

    /// <summary>Asks HDT to (re)load the collection; returns the result when HDT has one.</summary>
    HdtCollectionSnapshot? LoadCollection();

    RawCollectionSnapshot? GetRawCollection();

    AccountSnapshot? GetAccount();

    List<DeckExport>? GetInGameDecks();

    List<DeckExport>? GetHdtDecks();

    Dictionary<string, RankInfo>? GetRank();

    CardInfo? ResolveCard(int dbfId);

    /// <summary>The deck selected in HDT's deck list as a deck code, or null when none is selected.</summary>
    ActiveDeckInfo? GetActiveDeck();

    /// <summary>Subscribes to HDT's collection-changed event. Returns false when the event is unavailable.</summary>
    bool SubscribeCollectionChanged(Action handler);

    /// <summary>Subscribes to HDT's mode-changed event; the handler receives the mode name (e.g. <c>HUB</c>).</summary>
    bool SubscribeModeChanged(Action<string> handler);
}

/// <summary>HDT's selected deck, serialised the way the game would.</summary>
internal sealed class ActiveDeckInfo
{
    public string Name { get; set; } = string.Empty;

    public string Deckstring { get; set; } = string.Empty;
}
