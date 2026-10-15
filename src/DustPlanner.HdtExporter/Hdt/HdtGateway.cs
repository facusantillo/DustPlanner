using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using DustPlanner.Contracts;
using DustPlanner.HdtExporter.Logic;
using HearthMirror;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace DustPlanner.HdtExporter.Hdt;

/// <summary>
/// The only class that references HDT / HearthMirror / HearthDb types directly.
/// Each API is touched inside its own non-inlined method so that a missing type or member in the
/// running HDT build surfaces as a catchable <see cref="TypeLoadException"/> / <see cref="MissingMemberException"/>
/// for that call only, instead of preventing the plugin from loading.
/// Verified signatures: HDT 1.55.6 (binaries) and 1.59.2 (source) — see docs/VERIFICACION.md.
/// </summary>
internal sealed class HdtGateway : IHdtGateway
{
    private const int ReadTimeoutMs = 10000;

    public ExporterCapabilities Capabilities { get; } = new ExporterCapabilities
    {
        Collection = true,
        InGameDecks = true,
        HdtDecks = true,
        Rank = true,
        BattleTag = true,
        RawMirrorCollection = true,
    };

    public Version? HdtVersion => Guard(() => HdtAssemblyVersion(), "HDT version", _ => { });

    public Version? HearthMirrorVersion => Guard(() => MirrorAssemblyVersion(), "HearthMirror version", _ => { });

    public Version? HearthDbVersion => Guard(() => HearthDbAssemblyVersion(), "HearthDb version", _ => { });

    public bool IsGameRunning => GuardValue(() => GameIsRunning(), "Core.Game.IsRunning", _ => { }) ?? false;

    public HdtCollectionSnapshot? GetCachedCollection() =>
        Guard(() => ReadCachedCollection(), "CollectionHelpers.Hearthstone.TryGetCollection", c => c.Collection = false);

    public HdtCollectionSnapshot? LoadCollection() =>
        Guard(() => ReadLoadedCollection(), "CollectionHelpers.Hearthstone.GetCollection", c => c.Collection = false);

    public RawCollectionSnapshot? GetRawCollection() =>
        Guard(() => ReadRawCollection(), "Reflection.Client.GetFullCollection", c => c.RawMirrorCollection = false);

    public AccountSnapshot? GetAccount() =>
        Guard(() => ReadAccount(), "Reflection.Client.GetAccountId/GetBattleTag", c => c.BattleTag = false);

    public List<DeckExport>? GetInGameDecks() =>
        Guard(() => ReadInGameDecks(), "Reflection.Client.GetDecks", c => c.InGameDecks = false);

    public List<DeckExport>? GetHdtDecks() =>
        Guard(() => ReadHdtDecks(), "DeckList.Instance.Decks", c => c.HdtDecks = false);

    public Dictionary<string, RankInfo>? GetRank() =>
        Guard(() => ReadRank(), "Reflection.Client.GetMedalInfo", c => c.Rank = false);

    public CardInfo? ResolveCard(int dbfId) =>
        Guard(() => ReadCard(dbfId), "HearthDb.Cards.GetFromDbfId", _ => { });

    public bool SubscribeCollectionChanged(Action handler) =>
        GuardValue(() => { HookCollectionChanged(handler); return true; }, "CollectionHelpers.Hearthstone.OnCollectionChanged", c => c.Collection = false) ?? false;

    public bool SubscribeModeChanged(Action<string> handler) =>
        GuardValue(() => { HookModeChanged(handler); return true; }, "GameEvents.OnModeChanged", _ => { }) ?? false;

    // ---------------------------------------------------------------------------------------------
    // Capability guard
    // ---------------------------------------------------------------------------------------------

    private T? Guard<T>(Func<T?> call, string api, Action<ExporterCapabilities> degrade)
        where T : class
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (IsApiMismatch(ex))
        {
            degrade(Capabilities);
            PluginLog.Warn("HDT API '" + api + "' is not available in this HDT build; feature disabled. " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error("HDT API '" + api + "' failed.", ex);
            return null;
        }
    }

    private T? GuardValue<T>(Func<T> call, string api, Action<ExporterCapabilities> degrade)
        where T : struct
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (IsApiMismatch(ex))
        {
            degrade(Capabilities);
            PluginLog.Warn("HDT API '" + api + "' is not available in this HDT build; feature disabled. " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            PluginLog.Error("HDT API '" + api + "' failed.", ex);
            return null;
        }
    }

    private static bool IsApiMismatch(Exception ex) =>
        ex is TypeLoadException
        || ex is MissingMemberException
        || ex is FileNotFoundException
        || ex is BadImageFormatException
        || ex is NotImplementedException
        || ex is TypeInitializationException tie && tie.InnerException is not null && IsApiMismatch(tie.InnerException);

    // ---------------------------------------------------------------------------------------------
    // Direct HDT access (one API per method, never inlined)
    // ---------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Version HdtAssemblyVersion() => typeof(Hearthstone_Deck_Tracker.API.Core).Assembly.GetName().Version;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Version MirrorAssemblyVersion() => typeof(Reflection).Assembly.GetName().Version;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Version HearthDbAssemblyVersion() => typeof(HearthDb.Cards).Assembly.GetName().Version;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool GameIsRunning() => Hearthstone_Deck_Tracker.API.Core.Game.IsRunning;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HdtCollectionSnapshot? ReadCachedCollection() =>
        CollectionHelpers.Hearthstone.TryGetCollection(out var collection) && collection is not null
            ? ToSnapshot(collection)
            : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HdtCollectionSnapshot? ReadLoadedCollection()
    {
        // GetCollection() is async; the exporter already runs on a background thread, so blocking here is fine.
        var collection = CollectionHelpers.Hearthstone.GetCollection().GetAwaiter().GetResult();
        return collection is null ? null : ToSnapshot(collection);
    }

    private static HdtCollectionSnapshot ToSnapshot(Collection collection) => new HdtCollectionSnapshot
    {
        AccountHi = collection.AccountHi,
        AccountLo = collection.AccountLo,
        BattleTag = collection.BattleTag,
        Dust = collection.Dust,
        Cards = collection.Cards.ToDictionary(p => p.Key, p => (int[])p.Value.Clone()),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RawCollectionSnapshot? ReadRawCollection()
    {
        using (Reflection.ClientReadTimeout(ReadTimeoutMs))
        {
            var raw = Reflection.Client.GetFullCollection();
            if (raw is null)
            {
                return null;
            }

            var entries = new List<MirrorCardEntry>(raw.Cards.Count);
            foreach (var card in raw.Cards)
            {
                if (string.IsNullOrEmpty(card.Id))
                {
                    continue;
                }

                var dbfId = HearthDb.Cards.CardIdToDbfId.TryGetValue(card.Id, out var id) ? id : 0;
                entries.Add(new MirrorCardEntry(card.Id, dbfId, card.PremiumType, card.Count, card.TrialCount));
            }

            return new RawCollectionSnapshot { Dust = raw.Dust, Gold = raw.Gold, Cards = entries };
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AccountSnapshot? ReadAccount()
    {
        using (Reflection.ClientReadTimeout(ReadTimeoutMs))
        {
            var id = Reflection.Client.GetAccountId();
            if (id is null)
            {
                return null;
            }

            var tag = Reflection.Client.GetBattleTag();
            return new AccountSnapshot
            {
                Hi = id.Hi,
                Lo = id.Lo,
                BattleTag = tag is null ? null : tag.Name + "#" + tag.Number,
            };
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<DeckExport>? ReadInGameDecks()
    {
        List<HearthMirror.Objects.Deck>? decks;
        using (Reflection.ClientReadTimeout(ReadTimeoutMs))
        {
            decks = Reflection.Client.GetDecks();
        }

        if (decks is null)
        {
            return null;
        }

        var result = new List<DeckExport>(decks.Count);
        foreach (var deck in decks)
        {
            if (deck is null || deck.Cards is null)
            {
                continue;
            }

            var export = new DeckExport
            {
                Origin = DeckOrigins.InGame,
                ExternalId = deck.Id.ToString(CultureInfo.InvariantCulture),
                Name = deck.Name ?? string.Empty,
                HeroCardId = deck.Hero,
                HeroDbfId = DbfIdOf(deck.Hero),
                CardClass = ClassOf(deck.Hero),
                FormatType = deck.FormatType,
                DeckType = deck.Type,
                Archived = false,
                IsWild = null,
                LastEdited = null,
                Cards = deck.Cards.Where(c => c is not null && !string.IsNullOrEmpty(c.Id)).Select(ToDeckCard).ToList(),
            };

            if (deck.Sideboards is not null)
            {
                foreach (var pair in deck.Sideboards)
                {
                    export.Sideboards.Add(new SideboardExport
                    {
                        OwnerCardId = pair.Key,
                        OwnerDbfId = DbfIdOf(pair.Key) ?? 0,
                        Cards = (pair.Value ?? new List<HearthMirror.Objects.Card>()).Where(c => c is not null && !string.IsNullOrEmpty(c.Id)).Select(ToDeckCard).ToList(),
                    });
                }
            }

            result.Add(export);
        }

        return result;
    }

    private static DeckCardExport ToDeckCard(HearthMirror.Objects.Card card) => new DeckCardExport
    {
        CardId = card.Id,
        DbfId = DbfIdOf(card.Id) ?? 0,
        Count = card.Count,
        PremiumType = card.PremiumType,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<DeckExport>? ReadHdtDecks()
    {
        var decks = DeckList.Instance.Decks;
        if (decks is null)
        {
            return null;
        }

        var result = new List<DeckExport>();
        foreach (var deck in decks.ToList())
        {
            if (deck is null || deck.IsArenaDeck)
            {
                continue;
            }

            var version = deck.GetSelectedDeckVersion() ?? deck;
            var export = new DeckExport
            {
                Origin = DeckOrigins.Hdt,
                ExternalId = deck.DeckId.ToString("D"),
                Name = deck.Name ?? string.Empty,
                HeroCardId = null,
                HeroDbfId = null,
                CardClass = deck.Class,
                FormatType = null,
                DeckType = null,
                Archived = deck.Archived,
                IsWild = deck.IsWildDeck,
                LastEdited = deck.LastEdited == default ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(deck.LastEdited, DateTimeKind.Local)),
                Cards = version.Cards.Where(c => c is not null).Select(c => new DeckCardExport
                {
                    CardId = c.Id,
                    DbfId = c.DbfId,
                    Count = c.Count,
                    PremiumType = null,
                }).ToList(),
            };

            if (version.Sideboards is not null)
            {
                foreach (var sideboard in version.Sideboards)
                {
                    if (sideboard is null)
                    {
                        continue;
                    }

                    export.Sideboards.Add(new SideboardExport
                    {
                        OwnerCardId = sideboard.OwnerCardId,
                        OwnerDbfId = DbfIdOf(sideboard.OwnerCardId) ?? 0,
                        Cards = (sideboard.Cards ?? new List<Card>()).Where(c => c is not null).Select(c => new DeckCardExport
                        {
                            CardId = c.Id,
                            DbfId = c.DbfId,
                            Count = c.Count,
                            PremiumType = null,
                        }).ToList(),
                    });
                }
            }

            result.Add(export);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, RankInfo>? ReadRank()
    {
        HearthMirror.Objects.MedalInfo? medal;
        using (Reflection.ClientReadTimeout(ReadTimeoutMs))
        {
            medal = Reflection.Client.GetMedalInfo();
        }

        if (medal is null)
        {
            return null;
        }

        var result = new Dictionary<string, RankInfo>(StringComparer.Ordinal);
        Add(result, RankFormats.Standard, medal.Standard);
        Add(result, RankFormats.Wild, medal.Wild);
        Add(result, RankFormats.Classic, medal.Classic);
        Add(result, RankFormats.Twist, medal.Twist);
        return result;

        static void Add(Dictionary<string, RankInfo> target, string key, HearthMirror.Objects.MedalInfoData? data)
        {
            if (data is null)
            {
                return;
            }

            target[key] = new RankInfo
            {
                LeagueId = data.LeagueId,
                StarLevel = data.StarLevel,
                Stars = data.Stars,
                StarsPerWin = data.StarsPerWin,
                LegendRank = data.LegendRank,
                SeasonId = data.SeasonId,
                SeasonGames = data.SeasonGames,
                SeasonWins = data.SeasonWins,
                Streak = data.Streak,
                BestStarLevel = data.BestStarLevel,
                BestEverLeagueId = data.BestEverLeagueId,
                BestEverStarLevel = data.BestEverStarLevel,
            };
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CardInfo? ReadCard(int dbfId)
    {
        var card = HearthDb.Cards.GetFromDbfId(dbfId, false);
        return card is null
            ? null
            : new CardInfo(card.DbfId, card.Id, card.Name ?? card.Id, card.Rarity.ToString(), card.Set.ToString());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int? DbfIdOf(string? cardId) =>
        !string.IsNullOrEmpty(cardId) && HearthDb.Cards.CardIdToDbfId.TryGetValue(cardId, out var dbfId) ? dbfId : (int?)null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ClassOf(string? heroCardId)
    {
        if (string.IsNullOrEmpty(heroCardId) || !HearthDb.Cards.All.TryGetValue(heroCardId, out var hero))
        {
            return null;
        }

        var name = hero.Class.ToString();
        return name.Length == 0 ? null : char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HookCollectionChanged(Action handler) => CollectionHelpers.Hearthstone.OnCollectionChanged += handler;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HookModeChanged(Action<string> handler) => GameEvents.OnModeChanged.Add((Mode mode) => handler(mode.ToString()));
}
