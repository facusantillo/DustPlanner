using System.Text.Json;
using System.Threading.Channels;
using DustPlanner.App;
using DustPlanner.Contracts;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Json;
using DustPlanner.LogTracker;
using DustPlanner.Store;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Host;

/// <summary>
/// Polls (never watches, never hooks) four things the game and HDT write on their own — Power.log, the exporter's
/// <c>collection.json</c>, <c>rank.json</c> and <c>active-deck.json</c> — and publishes an <see cref="OverlayState"/>
/// whenever something changed. Completed games are stored in the database like <c>games import</c> does.
/// </summary>
public sealed class OverlayStateService : BackgroundService
{
    private readonly OverlayHostOptions _options;
    private readonly AppServices _app;
    private readonly ILogger<OverlayStateService> _log;
    private readonly object _gate = new();
    private readonly List<Channel<OverlayState>> _subscribers = new();
    private readonly TimeProvider _clock;

    private DustStore? _store;
    private GameRepository? _games;
    private PowerLogFollower? _follower;
    private string? _logsDirectory;
    private DateTime _collectionStamp;
    private DateTime _rankStamp;
    private DateTime _activeDeckStamp;
    private bool _syncPending;
    private readonly object _syncGate = new();
    private Task<CardCatalog?>? _catalogTask;
    private OverlayState _state = OverlayState.Empty;
    private RecordState _session = new();
    private LastGameState? _lastGame;
    private readonly DateTimeOffset _sessionStart;

    public OverlayStateService(OverlayHostOptions options, AppServices app, ILogger<OverlayStateService> log, TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
        _sessionStart = _clock.GetUtcNow();
    }

    public OverlayState Current => _state;

    public DateTimeOffset SessionStart => _sessionStart;

    /// <summary>Current state first, then every change, until cancelled.</summary>
    public async IAsyncEnumerable<OverlayState> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<OverlayState>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        try
        {
            yield return _state;
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var state))
                {
                    yield return state;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    /// <summary>
    /// Initialises synchronously here, not in <see cref="ExecuteAsync"/>: the host only reports "started" once every
    /// hosted service's StartAsync has returned, so the first HTTP request always sees a populated state. Whether
    /// ExecuteAsync runs inline or on the thread pool differs between runtimes (it raced the first request on Windows).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Initialize();
        }
#pragma warning disable CA1031 // the poll loop must survive anything: the failure is logged and shown on the status page
        catch (Exception ex)
        {
            Log.InitFailed(_log, ex);
            Publish(_state with { Warnings = new[] { $"Initialisation failed: {ex.GetType().Name}: {ex.Message}" } });
        }
#pragma warning restore CA1031

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Opens the database, locates the logs and starts the catalog load. Safe to call from tests.</summary>
    internal void Initialize()
    {
        Directory.CreateDirectory(_app.Paths.Root);
        _store = DustStore.Open(_app.Paths.Database);
        _games = new GameRepository(_store);
        _logsDirectory = _options.LogsDirectory ?? LogLocator.FindLogsDirectory();
        if (_logsDirectory is not null)
        {
            _follower = new PowerLogFollower(_logsDirectory);
            _follower.GameCompleted += OnGameCompleted;
        }

        _catalogTask = Task.Run(async () =>
        {
            try
            {
                return (await _app.LoadCatalogAsync(forceRefresh: false, "enUS", CancellationToken.None).ConfigureAwait(false)).Catalog;
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is InvalidOperationException || ex is JsonException)
            {
                Log.CatalogUnavailable(_log, ex);
                return null;
            }
        });

        Tick();
    }

    /// <summary>One poll: inbox files, Power.log, derived state. Publishes only when something changed.</summary>
    internal void Tick()
    {
        var warnings = new List<string>();
        var state = _state;

        try
        {
            state = RefreshAccount(state, warnings);
            state = RefreshInbox(state, warnings);
            state = RefreshGames(state, warnings);
        }
#pragma warning disable CA1031 // same: one bad poll must not kill the host
        catch (Exception ex)
        {
            Log.PollFailed(_log, ex);
            warnings.Add($"Poll failed: {ex.GetType().Name}: {ex.Message}");
        }
#pragma warning restore CA1031

        state = state with { Warnings = warnings, UpdatedAt = _clock.GetUtcNow() };
        if (!SameContent(state, _state))
        {
            Publish(state);
        }
    }

    /// <summary>
    /// Record equality is not enough: <see cref="OverlayState.Warnings"/> is a list (reference equality) and would
    /// make every poll look like a change. Compare the serialised content instead, ignoring the timestamp.
    /// </summary>
    internal static bool SameContent(OverlayState a, OverlayState b) =>
        JsonSerializer.Serialize(a with { UpdatedAt = default }, CoreJson.Options) == JsonSerializer.Serialize(b with { UpdatedAt = default }, CoreJson.Options);

    private OverlayState RefreshAccount(OverlayState state, List<string> warnings)
    {
        if (state.AccountKey is not null)
        {
            return state;
        }

        var accounts = InboxLocator.ListAccounts(_app.Paths.Inbox);
        var (selected, _) = InboxLocator.Select(accounts, _options.Account);
        if (selected is not null)
        {
            return state with { AccountKey = selected.AccountKey, BattleTag = selected.BattleTag };
        }

        var fromGames = _options.Account ?? _games?.LatestAccount();
        if (fromGames is not null)
        {
            return state with { AccountKey = fromGames };
        }

        warnings.Add("No account yet: install the HDT exporter plugin (collection, rank, dust) or play a game with Power.log enabled.");
        return state;
    }

    private OverlayState RefreshInbox(OverlayState state, List<string> warnings)
    {
        if (state.AccountKey is null)
        {
            return state;
        }

        var dir = Path.Combine(_app.Paths.Inbox, state.AccountKey);
        var collectionFile = Path.Combine(dir, ExportSchema.CollectionFileName);
        if (File.Exists(collectionFile))
        {
            var stamp = File.GetLastWriteTimeUtc(collectionFile);
            if (stamp != _collectionStamp)
            {
                _collectionStamp = stamp;
                _syncPending = true;
                state = state with { Dust = ReadDust(collectionFile) };
            }
        }
        else
        {
            warnings.Add("No collection export yet for this account; dust is unknown until the HDT plugin exports.");
        }

        if (_syncPending && _catalogTask is { IsCompletedSuccessfully: true, Result: not null })
        {
            // The exporter wrote a new collection: import it now so plans are matched without any user action.
            _syncPending = false;
            try
            {
                state = state with { LastSync = RunSync(state.AccountKey).State };
            }
            catch (Exception ex) when (ex is AppException || ex is IOException || ex is InvalidDataException || ex is JsonException || ex is Microsoft.Data.Sqlite.SqliteException)
            {
                warnings.Add("Automatic sync failed: " + ex.Message);
                state = _state;
            }
        }

        var rankFile = Path.Combine(dir, ExportSchema.RankFileName);
        if (File.Exists(rankFile))
        {
            var stamp = File.GetLastWriteTimeUtc(rankFile);
            if (stamp != _rankStamp)
            {
                _rankStamp = stamp;
                var export = JsonSerializer.Deserialize<RankExport>(File.ReadAllText(rankFile), CoreJson.Options);
                if (export is not null)
                {
                    if (_store is not null)
                    {
                        // rank_sample references account: make sure the row exists (sync does the same before importing).
                        new SnapshotRepository(_store).UpsertAccount(export.Meta.Account.AccountKey, export.Meta.Account.BattleTag, export.Meta.Account.Region, export.Meta.ExportedAt);
                        new RankRepository(_store).Import(export);
                    }

                    state = state with { Rank = ToRankState(export) };
                }
            }
        }
        else
        {
            warnings.Add("No rank export yet; the rank appears after HDT sees the Play screen with the exporter plugin enabled.");
        }

        // No warning when the file is missing: older exporters never write it, and the overlay simply hides the cell.
        var activeDeckFile = Path.Combine(dir, ExportSchema.ActiveDeckFileName);
        if (File.Exists(activeDeckFile))
        {
            var stamp = File.GetLastWriteTimeUtc(activeDeckFile);
            if (stamp != _activeDeckStamp)
            {
                _activeDeckStamp = stamp;
                var export = JsonSerializer.Deserialize<ActiveDeckExport>(File.ReadAllText(activeDeckFile), CoreJson.Options);
                if (export is not null)
                {
                    if (_store is not null)
                    {
                        new ActiveDeckRepository(_store).Import(export);
                    }

                    state = state with { ActiveDeck = ToActiveDeckState(export) };
                }
            }
        }

        return state;
    }

    private static ActiveDeckState? ToActiveDeckState(ActiveDeckExport export) =>
        export.Deck is { } deck && !string.IsNullOrEmpty(deck.Deckstring)
            ? new ActiveDeckState(deck.HdtDeckId, deck.Name, deck.Deckstring, deck.CardClass?.ToUpperInvariant(), deck.Format, export.Meta.ExportedAt)
            : null;

    /// <summary>
    /// Imports the account's export into the database (collection snapshot, decks, rank) and matches stored plans,
    /// exactly like <c>dustplanner sync</c>. Runs automatically when the exporter writes a new collection and on
    /// request from the panel; one at a time.
    /// </summary>
    public SyncOutcome RunSync(string? account)
    {
        var catalog = _catalogTask is { IsCompletedSuccessfully: true } t ? t.Result : null;
        var now = _clock.GetUtcNow();
        if (catalog is null)
        {
            throw new AppException("Card catalog not loaded yet; try again in a moment.");
        }

        lock (_syncGate)
        {
            try
            {
                var summaries = _app.SyncInbox(catalog, account, null, null, AppServices.Today, now);
                var mine = summaries.FirstOrDefault(s => account is null || string.Equals(s.AccountKey, account, StringComparison.Ordinal)) ?? summaries[0];
                var result = new SyncState(now, mine.SnapshotTakenAt, mine.NewSnapshot, mine.Dust, mine.DustDelta, mine.Events.Count, mine.Events.Count(e => e.PlanId is not null), null);
                Log.Synced(_log, mine.Account, mine.NewSnapshot, mine.Events.Count);
                Publish(_state with { LastSync = result, UpdatedAt = now });
                return new SyncOutcome(result, summaries);
            }
            catch (Exception ex) when (ex is AppException || ex is IOException || ex is InvalidDataException || ex is JsonException || ex is Microsoft.Data.Sqlite.SqliteException)
            {
                Log.SyncFailed(_log, ex);
                Publish(_state with { LastSync = new SyncState(now, default, false, 0, 0, 0, 0, ex.Message), UpdatedAt = now });
                throw;
            }
        }
    }

    /// <summary>Forces the next poll to re-read the exporter files and sync (after the panel changed something).</summary>
    public void RequestSync() => _syncPending = true;

    private static DustState? ReadDust(string collectionFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(collectionFile));
        var root = doc.RootElement;
        if (!root.TryGetProperty("dust", out var dust) || dust.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var takenAt = root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("exported_at", out var at) && at.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        return new DustState(dust.GetInt32(), takenAt);
    }

    private static RankState? ToRankState(RankExport export)
    {
        foreach (var format in new[] { RankFormats.Standard, RankFormats.Wild, RankFormats.Twist, RankFormats.Classic })
        {
            if (export.Formats.TryGetValue(format, out var info) && (info.LeagueId > 0 || info.LegendRank > 0))
            {
                var sample = new RankSample(export.Meta.Account.AccountKey, export.Meta.ExportedAt, format, info.LeagueId, info.StarLevel, info.Stars, info.LegendRank, info.SeasonId, info.SeasonGames, info.SeasonWins);
                return new RankState(RankRepository.Describe(sample), format, info.LeagueId, info.StarLevel, info.Stars, info.LegendRank, export.Meta.ExportedAt);
            }
        }

        return null;
    }

    private OverlayState RefreshGames(OverlayState state, List<string> warnings)
    {
        if (_follower is null)
        {
            warnings.Add("Hearthstone's Logs folder was not found; pass --logs <path-to-Hearthstone\\Logs>. Run 'dustplanner games log-config' to check that Power.log is enabled.");
            return state with { Session = _session };
        }

        _follower.Poll();
        if (LogLocator.FindPowerLogs(_logsDirectory!).Count == 0)
        {
            warnings.Add("No Power.log in " + _logsDirectory + " yet; start Hearthstone (and check 'dustplanner games log-config').");
        }

        var catalog = _catalogTask is { IsCompletedSuccessfully: true } t ? t.Result : null;
        var current = _follower.CurrentGame;
        CurrentGameState? currentState = null;
        if (current is not null)
        {
            var friendly = current.Friendly;
            var opponent = current.Opponent;
            currentState = new CurrentGameState(current.StartedAt, current.GameType, current.FormatType,
                HeroClasses.ClassOf(friendly?.HeroCardId, catalog), HeroClasses.ClassOf(opponent?.HeroCardId, catalog), opponent?.Name, current.Turns);
        }

        var today = _games is not null && state.AccountKey is not null ? TodayRecord(state.AccountKey) : new RecordState();
        return state with { Session = _session, Today = today, CurrentGame = currentState, LastGame = _lastGame ?? state.LastGame };
    }

    private void OnGameCompleted(GameRecord game)
    {
        var friendly = game.Friendly;
        if (friendly is null)
        {
            Log.NoLocalPlayer(_log, game.StartedAt);
            return;
        }

        var accountKey = friendly.AccountKey;
        if (_state.AccountKey is null)
        {
            _state = _state with { AccountKey = accountKey };
        }
        else if (!string.Equals(accountKey, _state.AccountKey, StringComparison.Ordinal))
        {
            Log.OtherAccount(_log, accountKey);
        }

        var catalog = _catalogTask is { IsCompletedSuccessfully: true } t ? t.Result : null;
        _games?.Insert(game, catalog);

        if (string.Equals(accountKey, _state.AccountKey, StringComparison.Ordinal))
        {
            _session = _session.Add(game.Result);
            _lastGame = new LastGameState(game.EndedAt ?? game.StartedAt, game.Result, HeroClasses.ClassOf(friendly.HeroCardId, catalog),
                HeroClasses.ClassOf(game.Opponent?.HeroCardId, catalog), game.Opponent?.Name, game.Turns);
        }
    }

    private RecordState TodayRecord(string accountKey)
    {
        var midnight = new DateTimeOffset(_clock.GetLocalNow().Date, _clock.LocalTimeZone.GetUtcOffset(_clock.GetLocalNow())).ToUniversalTime();
        var stats = _games!.Stats(accountKey, midnight);
        return new RecordState { Wins = stats.Wins, Losses = stats.Losses, Ties = stats.Ties, Streak = _session.Streak };
    }

    private void Publish(OverlayState state)
    {
        _state = state;
        List<Channel<OverlayState>> targets;
        lock (_gate)
        {
            targets = _subscribers.ToList();
        }

        foreach (var channel in targets)
        {
            channel.Writer.TryWrite(state);
        }
    }

    public override void Dispose()
    {
        _store?.Dispose();
        base.Dispose();
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Host initialisation failed")]
    public static partial void InitFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Card catalog unavailable; hero classes fall back to the built-in table")]
    public static partial void CatalogUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Poll failed")]
    public static partial void PollFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Completed game at {Start} has no identifiable local player; skipped")]
    public static partial void NoLocalPlayer(ILogger logger, DateTimeOffset start);

    [LoggerMessage(Level = LogLevel.Information, Message = "Game of another account ({Account}) stored but not counted in the session")]
    public static partial void OtherAccount(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced {Account}: new snapshot {NewSnapshot}, {Events} change(s)")]
    public static partial void Synced(ILogger logger, string account, bool newSnapshot, int events);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync failed")]
    public static partial void SyncFailed(ILogger logger, Exception ex);
}
