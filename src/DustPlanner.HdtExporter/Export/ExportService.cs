using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using DustPlanner.Contracts;
using DustPlanner.HdtExporter.Hdt;
using DustPlanner.HdtExporter.Logic;

namespace DustPlanner.HdtExporter.Export;

/// <summary>Builds the export DTOs from the gateway snapshots and writes them. One export at a time.</summary>
internal sealed class ExportService
{
    private readonly IHdtGateway _hdt;
    private readonly ExporterSettings _settings;
    private readonly ExportWriter _writer;
    private readonly ExportThrottle _collectionThrottle;
    private readonly ExportThrottle _auxiliaryThrottle;
    private readonly object _gate = new object();
    private readonly string _exporterVersion;

    public ExportService(IHdtGateway hdt, ExporterSettings settings)
    {
        _hdt = hdt;
        _settings = settings;
        _writer = new ExportWriter(settings.InboxDirectory, settings.HistoryKeep);
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.MinIntervalSeconds));
        _collectionThrottle = new ExportThrottle(interval);
        _auxiliaryThrottle = new ExportThrottle(interval);
        _exporterVersion = typeof(ExportService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    public string LastStatus { get; private set; } = "Not run yet";

    public DateTimeOffset? LastExportAt { get; private set; }

    /// <summary>Exports collection, decks and rank. Returns a short status line.</summary>
    public string ExportAll(string trigger, bool manual)
    {
        if (!Monitor.TryEnter(_gate, 0))
        {
            return "Export already running";
        }

        try
        {
            var now = DateTimeOffset.Now;
            var parts = new List<string>();

            var collection = manual ? _hdt.LoadCollection() ?? _hdt.GetCachedCollection() : _hdt.GetCachedCollection();
            var account = ResolveAccount(collection);
            if (account is null)
            {
                return SetStatus("No account information yet (is Hearthstone running and past the login screen?)");
            }

            var accountKey = AccountKeys.Format(account.Hi, account.Lo);

            if (collection is not null)
            {
                var input = new CollectionInput { HdtCards = collection.Cards, Dust = collection.Dust };
                var export = CollectionExportBuilder.Build(input, NewMeta(ExportSchema.CollectionKind, trigger, account, now));
                var decision = _collectionThrottle.Decide(now, export.ContentHash, manual);
                if (decision == ThrottleDecision.Run)
                {
                    _writer.Write(export, ExportSchema.CollectionKind, accountKey, now, keepHistory: true);
                    _collectionThrottle.MarkRan(now, export.ContentHash);
                    parts.Add("collection (" + export.Cards.Count.ToString(CultureInfo.InvariantCulture) + " cards, " + export.Dust.ToString(CultureInfo.InvariantCulture) + " dust)");
                }
                else
                {
                    parts.Add("collection skipped: " + decision);
                }
            }
            else
            {
                parts.Add("collection unavailable");
            }

            if (manual || _auxiliaryThrottle.Decide(now, null, manual) == ThrottleDecision.Run)
            {
                parts.Add(ExportDecks(trigger, account, accountKey, now));
                parts.Add(ExportRank(trigger, account, accountKey, now));
                _auxiliaryThrottle.MarkRan(now, null);
            }

            LastExportAt = now;
            return SetStatus(string.Join("; ", parts));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Export failed.", ex);
            return SetStatus("Export failed: " + ex.Message);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Writes <c>diagnostics.json</c> from the raw HearthMirror collection.</summary>
    public string ExportDiagnostics()
    {
        if (!Monitor.TryEnter(_gate, 0))
        {
            return "Export already running";
        }

        try
        {
            var now = DateTimeOffset.Now;
            var collection = _hdt.LoadCollection() ?? _hdt.GetCachedCollection();
            var account = ResolveAccount(collection);
            if (account is null || collection is null)
            {
                return SetStatus("Diagnostics need Hearthstone running with the collection loaded (open the Collection once).");
            }

            var raw = _hdt.GetRawCollection();
            var input = new CollectionInput
            {
                HdtCards = collection.Cards,
                Dust = collection.Dust,
                Gold = raw?.Gold,
                RawCards = raw?.Cards,
            };

            var meta = NewMeta(ExportSchema.DiagnosticsKind, "manual", account, now);
            var diagnostics = DiagnosticsBuilder.Build(input, _hdt.ResolveCard, meta);
            diagnostics.Notes.Insert(0, "HDT " + (meta.HdtVersion ?? "?") + ", HearthMirror " + (meta.HearthMirrorVersion ?? "?") + ", HearthDb " + (meta.HearthDbVersion ?? "?"));
            var path = _writer.Write(diagnostics, ExportSchema.DiagnosticsKind, AccountKeys.Format(account.Hi, account.Lo), now, keepHistory: false);
            return SetStatus("Diagnostics written to " + path);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Diagnostics export failed.", ex);
            return SetStatus("Diagnostics failed: " + ex.Message);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private string ExportDecks(string trigger, AccountSnapshot account, string accountKey, DateTimeOffset now)
    {
        var decks = new List<DeckExport>();
        var inGame = _hdt.GetInGameDecks();
        if (inGame is not null)
        {
            decks.AddRange(inGame);
        }

        if (_settings.IncludeHdtDecks)
        {
            var hdtDecks = _hdt.GetHdtDecks();
            if (hdtDecks is not null)
            {
                decks.AddRange(hdtDecks);
            }
        }

        if (decks.Count == 0)
        {
            return "decks unavailable (visit the Play screen once)";
        }

        var export = new DecksExport { Meta = NewMeta(ExportSchema.DecksKind, trigger, account, now), Decks = decks };
        _writer.Write(export, ExportSchema.DecksKind, accountKey, now, keepHistory: true);
        return "decks (" + decks.Count.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private string ExportRank(string trigger, AccountSnapshot account, string accountKey, DateTimeOffset now)
    {
        var rank = _hdt.GetRank();
        if (rank is null || rank.Count == 0)
        {
            return "rank unavailable";
        }

        var export = new RankExport { Meta = NewMeta(ExportSchema.RankKind, trigger, account, now), Formats = rank };
        _writer.Write(export, ExportSchema.RankKind, accountKey, now, keepHistory: true);
        var standard = rank.TryGetValue(RankFormats.Standard, out var s) ? s : null;
        return standard is null
            ? "rank"
            : "rank (standard league " + standard.LeagueId.ToString(CultureInfo.InvariantCulture) + ", star level " + standard.StarLevel.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private AccountSnapshot? ResolveAccount(HdtCollectionSnapshot? collection)
    {
        if (collection is not null && (collection.AccountHi != 0 || collection.AccountLo != 0))
        {
            return new AccountSnapshot { Hi = collection.AccountHi, Lo = collection.AccountLo, BattleTag = collection.BattleTag };
        }

        return _hdt.IsGameRunning ? _hdt.GetAccount() : null;
    }

    private ExportMeta NewMeta(string kind, string trigger, AccountSnapshot account, DateTimeOffset now) => new ExportMeta
    {
        SchemaVersion = ExportSchema.Version,
        Kind = kind,
        ExportedAt = now,
        ExporterVersion = _exporterVersion,
        HdtVersion = _hdt.HdtVersion?.ToString(),
        HearthMirrorVersion = _hdt.HearthMirrorVersion?.ToString(),
        HearthDbVersion = _hdt.HearthDbVersion?.ToString(),
        Trigger = trigger,
        Account = new AccountRef
        {
            Hi = account.Hi,
            Lo = account.Lo,
            BattleTag = account.BattleTag,
            Region = AccountKeys.RegionFromHi(account.Hi),
        },
        Capabilities = Copy(_hdt.Capabilities),
    };

    private static ExporterCapabilities Copy(ExporterCapabilities source) => new ExporterCapabilities
    {
        Collection = source.Collection,
        InGameDecks = source.InGameDecks,
        HdtDecks = source.HdtDecks,
        Rank = source.Rank,
        BattleTag = source.BattleTag,
        RawMirrorCollection = source.RawMirrorCollection,
    };

    private string SetStatus(string status)
    {
        LastStatus = status;
        PluginLog.Info(status);
        return status;
    }
}
