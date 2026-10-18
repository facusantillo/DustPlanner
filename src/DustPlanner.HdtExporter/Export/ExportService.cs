using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    private readonly ExportThrottle _activeDeckThrottle;
    private readonly object _gate = new object();
    private readonly object _activeDeckGate = new object();
    private readonly string _exporterVersion;
    private readonly string _lastAccountPath;
    private AccountSnapshot? _rememberedAccount;
    private bool _rememberedAccountLoaded;

    public ExportService(IHdtGateway hdt, ExporterSettings settings)
    {
        _hdt = hdt;
        _settings = settings;
        _writer = new ExportWriter(settings.InboxDirectory, settings.HistoryKeep);
        _lastAccountPath = Path.Combine(settings.ResolvedRootDirectory, LastAccount.FileName);
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.MinIntervalSeconds));
        _collectionThrottle = new ExportThrottle(interval);
        _auxiliaryThrottle = new ExportThrottle(interval);
        _activeDeckThrottle = new ExportThrottle(interval);
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

            // Content-hashed, so this is free when the selected deck did not change; the menu transitions that export
            // are also when a streamer typically switches decks.
            parts.Add(ExportActiveDeck(trigger, account, accountKey, now, manual));

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

    /// <summary>
    /// Writes <c>active-deck.json</c> on its own (game start, deck selection). Resolves the account from HDT's cached
    /// collection (or HearthMirror's account id while the game runs) without forcing a collection load; skipped while
    /// nothing identifies the account yet.
    /// </summary>
    public string ExportActiveDeckOnly(string trigger)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var account = ResolveAccount(_hdt.GetCachedCollection());
            if (account is null)
            {
                return SetStatus("Active deck not exported: no account yet (open Hearthstone once; the plugin remembers the account afterwards)");
            }

            var status = ExportActiveDeck(trigger, account, AccountKeys.Format(account.Hi, account.Lo), now, manual: false);
            LastExportAt = now;
            return SetStatus(status);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Active deck export failed.", ex);
            return SetStatus("Active deck export failed: " + ex.Message);
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

    private string ExportActiveDeck(string trigger, AccountSnapshot account, string accountKey, DateTimeOffset now, bool manual)
    {
        lock (_activeDeckGate)
        {
            var info = _hdt.GetActiveDeck();
            if (info is null && !_hdt.Capabilities.ActiveDeck)
            {
                return "active deck unavailable";
            }

            var export = ActiveDeckExportBuilder.Build(info?.ToInput(), NewMeta(ExportSchema.ActiveDeckKind, trigger, account, now));
            var hash = ActiveDeckExportBuilder.ContentHash(export.Deck);
            var decision = _activeDeckThrottle.Decide(now, hash, manual);
            if (decision != ThrottleDecision.Run)
            {
                return "active deck skipped: " + decision;
            }

            _writer.Write(export, ExportSchema.ActiveDeckKind, accountKey, now, keepHistory: false);
            _activeDeckThrottle.MarkRan(now, hash);
            return export.Deck is null ? "active deck (none)" : "active deck (" + export.Deck.Name + ")";
        }
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

    /// <summary>
    /// The live account when HearthMirror has one (collection cache, then the running game); otherwise the last one it
    /// reported, kept in <c>last-account.txt</c>. Without the fallback, a deck chosen in HDT while Hearthstone is closed
    /// was never exported ("no account information yet") — found in VERIFICACION K7.
    /// </summary>
    private AccountSnapshot? ResolveAccount(HdtCollectionSnapshot? collection)
    {
        AccountSnapshot? live = null;
        if (collection is not null && (collection.AccountHi != 0 || collection.AccountLo != 0))
        {
            live = new AccountSnapshot { Hi = collection.AccountHi, Lo = collection.AccountLo, BattleTag = collection.BattleTag };
        }
        else if (_hdt.IsGameRunning)
        {
            live = _hdt.GetAccount();
        }

        if (live is not null && (live.Hi != 0 || live.Lo != 0))
        {
            Remember(live);
            return live;
        }

        return Remembered();
    }

    private void Remember(AccountSnapshot account)
    {
        var current = Remembered();
        if (current is not null && current.Hi == account.Hi && current.Lo == account.Lo && string.Equals(current.BattleTag, account.BattleTag, StringComparison.Ordinal))
        {
            return;
        }

        _rememberedAccount = account;
        _rememberedAccountLoaded = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lastAccountPath)!);
            File.WriteAllText(_lastAccountPath, LastAccount.Serialize(account.Hi, account.Lo, account.BattleTag));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            PluginLog.Warn("Could not remember the account: " + ex.Message);
        }
    }

    private AccountSnapshot? Remembered()
    {
        if (_rememberedAccountLoaded)
        {
            return _rememberedAccount;
        }

        _rememberedAccountLoaded = true;
        try
        {
            if (File.Exists(_lastAccountPath) && LastAccount.TryParse(File.ReadAllText(_lastAccountPath), out var hi, out var lo, out var tag))
            {
                _rememberedAccount = new AccountSnapshot { Hi = hi, Lo = lo, BattleTag = tag };
            }
            else
            {
                // Before the file exists (first run of this version): the inbox already names the account when there is exactly one.
                _rememberedAccount = AccountFromInbox();
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            PluginLog.Warn("Could not read the remembered account: " + ex.Message);
        }

        if (_rememberedAccount is not null)
        {
            PluginLog.Info("Using the remembered account " + (_rememberedAccount.BattleTag ?? AccountKeys.Format(_rememberedAccount.Hi, _rememberedAccount.Lo)) + " while Hearthstone is not running.");
        }

        return _rememberedAccount;
    }

    private AccountSnapshot? AccountFromInbox()
    {
        if (!Directory.Exists(_settings.InboxDirectory))
        {
            return null;
        }

        AccountSnapshot? found = null;
        foreach (var dir in Directory.GetDirectories(_settings.InboxDirectory))
        {
            if (!AccountKeys.TryParse(Path.GetFileName(dir), out var hi, out var lo) || !File.Exists(Path.Combine(dir, ExportSchema.CollectionFileName)))
            {
                continue;
            }

            if (found is not null)
            {
                return null; // two accounts: no guessing, the next game start settles it
            }

            found = new AccountSnapshot { Hi = hi, Lo = lo, BattleTag = ReadBattleTag(Path.Combine(dir, ExportSchema.CollectionFileName)) };
        }

        return found;
    }

    private static string? ReadBattleTag(string collectionFile)
    {
        try
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<CollectionExport>(File.ReadAllText(collectionFile), JsonConventions.Settings)?.Meta?.Account?.BattleTag;
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is IOException)
        {
            return null;
        }
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
        ActiveDeck = source.ActiveDeck,
    };

    private string SetStatus(string status)
    {
        LastStatus = status;
        PluginLog.Info(status);
        return status;
    }
}
