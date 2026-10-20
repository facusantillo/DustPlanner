using DustPlanner.App;
using DustPlanner.Core.Planner;
using DustPlanner.Kick;
using DustPlanner.LogTracker;
using DustPlanner.Store;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Host;

/// <summary>
/// Stage K7 (PRD §5.6): what the service does with the stream's own events.
/// <list type="bullet">
/// <item><c>stream.status</c> live → a <c>stream_session</c> row opens (with the rank of the moment); not live → it closes,
/// and the end-of-stream line goes to chat when the streamer turned that on.</item>
/// <item>A game that ends while live → the prediction reminder: a <c>kick.reminder</c> notice for the panel and the
/// status page, plus the optional chat line.</item>
/// <item><c>reward.redemption</c> → a <c>kick.redemption</c> notice for the overlay, once per redemption id, when shown.</item>
/// </list>
/// Nothing here touches the game or the system: it writes one table and emits notices.
/// </summary>
internal sealed partial class KickStreamService : BackgroundService
{
    private static readonly TimeSpan RedemptionMemory = TimeSpan.FromHours(1);

    private readonly OverlayStateService _overlay;
    private readonly KickConnector _connector;
    private readonly AppServices _services;
    private readonly KickNotices _notices;
    private readonly ILogger<KickStreamService> _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _seenRedemptions = new(StringComparer.Ordinal);
    private (bool IsLive, DateTimeOffset? StartedAt)? _lastLive;
    private DateTimeOffset? _lastGameEndedAt;
    private bool _lastGameInitialised;

    public KickStreamService(OverlayStateService overlay, KickConnector connector, AppServices services, KickNotices notices, ILogger<KickStreamService> log, TimeProvider? clock = null)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnKick(KickConnectionState state) => _ = OnLiveAsync(state.Live, stoppingToken);
        void OnRedemption(RewardRedemptionMessage redemption) => OnRedemptionReceived(redemption);
        _connector.StateChanged += OnKick;
        _connector.RedemptionReceived += OnRedemption;
        try
        {
            await OnLiveAsync(_connector.Current.Live, stoppingToken).ConfigureAwait(false);
            await foreach (var state in _overlay.Subscribe(stoppingToken).ConfigureAwait(false))
            {
                await OnOverlayAsync(state, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            _connector.StateChanged -= OnKick;
            _connector.RedemptionReceived -= OnRedemption;
        }
    }

    // ------------------------------------------------------------------------------------------------ sessions

    private async Task OnLiveAsync(LiveStatus? live, CancellationToken ct)
    {
        if (live is null)
        {
            return;
        }

        var key = (live.IsLive, live.StartedAt);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastLive == key)
            {
                return;
            }

            var account = _overlay.Current.AccountKey;
            if (account is null)
            {
                // No account yet (first start, inbox empty): the next overlay state retries through OnOverlayAsync.
                return;
            }

            _lastLive = key;
            if (live.IsLive)
            {
                OpenSession(account, live);
            }
            else
            {
                await CloseSessionAsync(account, live, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException || ex is IOException)
        {
            Log.SessionFailed(_log, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OpenSession(string account, LiveStatus live)
    {
        var slug = _connector.Current.Channel?.Slug ?? string.Empty;
        using var store = DustStore.Open(_services.Paths.Database);
        var sessions = new StreamSessionRepository(store);
        var startedAt = live.StartedAt ?? _clock.GetUtcNow();
        var id = sessions.Open(account, slug, startedAt, live.Title, CurrentRank(store, account));
        Log.SessionOpened(_log, id, startedAt);
    }

    private async Task CloseSessionAsync(string account, LiveStatus live, CancellationToken ct)
    {
        StreamSession? closed;
        (ChatPrefs Chat, KickPrefs Kick) prefs;
        using (var store = DustStore.Open(_services.Paths.Database))
        {
            var sessions = new StreamSessionRepository(store);
            // The overlay may follow another account by now (a change of region mid-stream): the session that is open
            // is the one to close, whichever account opened it.
            var open = sessions.LatestOpen(account) ?? sessions.LatestOpen(null);
            if (open is null)
            {
                return;
            }

            var endedAt = live.EndedAt ?? _clock.GetUtcNow();
            if (endedAt < open.StartedAt)
            {
                endedAt = _clock.GetUtcNow();
            }

            sessions.Close(open.Id, endedAt, CurrentRank(store, open.AccountKey));
            closed = sessions.Get(open.Id);
            prefs = Api.ChatEndpoints.ReadPrefs(_services);
            Log.SessionClosed(_log, open.Id, closed?.Record.Wins ?? 0, closed?.Record.Losses ?? 0);
        }

        if (closed is null || !prefs.Kick.PostStreamSummary || closed.SummaryPostedAt is not null || closed.Record.Games == 0)
        {
            return;
        }

        var language = ChatResponder.ResolveLanguage(prefs.Chat, null);
        var text = ChatFormat.Fit(ChatTexts.StreamSummary(language, closed.Record.Wins, closed.Record.Losses, closed.Record.Ties, ToChatRank(closed.RankStart), ToChatRank(closed.RankEnd)));
        if (await _connector.SendChatAsync(text, null, ct).ConfigureAwait(false))
        {
            using var store = DustStore.Open(_services.Paths.Database);
            new StreamSessionRepository(store).MarkSummaryPosted(closed.Id, _clock.GetUtcNow());
            Log.SummaryPosted(_log, closed.Id);
        }
    }

    private SessionRank? CurrentRank(DustStore store, string account)
    {
        var format = _overlay.Current.Rank?.Format ?? "standard";
        var sample = new RankRepository(store).Latest(account, format);
        return sample is null ? null : SessionRank.From(sample);
    }

    private static ChatRank? ToChatRank(SessionRank? rank) => rank is null ? null : new ChatRank(rank.LeagueId, rank.StarLevel, 0, rank.LegendRank, rank.Format);

    // ------------------------------------------------------------------------------------------------ prediction reminder

    private async Task OnOverlayAsync(OverlayState state, CancellationToken ct)
    {
        // A session that could not open for lack of an account gets another chance once the inbox names one.
        if (_lastLive is null && state.AccountKey is not null && _connector.Current.Live is { } live)
        {
            await OnLiveAsync(live, ct).ConfigureAwait(false);
        }

        var endedAt = state.LastGame?.EndedAt;
        if (!_lastGameInitialised)
        {
            // The game that was already finished when the service started is old news, never a reminder.
            _lastGameInitialised = true;
            _lastGameEndedAt = endedAt;
            return;
        }

        if (endedAt is null || endedAt == _lastGameEndedAt)
        {
            return;
        }

        _lastGameEndedAt = endedAt;
        if (_connector.Current.Live?.IsLive != true)
        {
            return;
        }

        var prefs = Api.ChatEndpoints.ReadPrefs(_services);
        if (prefs.Kick.PredictionReminder == KickPrefs.ReminderOff)
        {
            return;
        }

        var game = state.LastGame!;
        _notices.Publish(new KickNotice(KickNotice.Reminder, new { result = game.Result, player_class = game.PlayerClass, opponent_class = game.OpponentClass, ended_at = game.EndedAt }, _clock.GetUtcNow()));
        Log.Reminder(_log, game.Result);
        if (prefs.Kick.PredictionReminder == KickPrefs.ReminderPanelAndChat)
        {
            var language = ChatResponder.ResolveLanguage(prefs.Chat, null);
            await _connector.SendChatAsync(ChatFormat.Fit(ChatTexts.PredictionReminder(language, game.Result)), null, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------------ redemptions

    private void OnRedemptionReceived(RewardRedemptionMessage redemption)
    {
        var prefs = Api.ChatEndpoints.ReadPrefs(_services);
        if (!prefs.Kick.ShowRedemptions || (prefs.Kick.RedemptionFilter.Count > 0 && !prefs.Kick.RedemptionFilter.Contains(redemption.RewardId, StringComparer.Ordinal)))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        lock (_seenRedemptions)
        {
            // Once per redemption id: pending → accepted must not show twice (the relay dedupes too; belt and braces).
            foreach (var stale in _seenRedemptions.Where(p => now - p.Value > RedemptionMemory).Select(p => p.Key).ToList())
            {
                _seenRedemptions.Remove(stale);
            }

            if (redemption.Id.Length > 0 && !_seenRedemptions.TryAdd(redemption.Id, now))
            {
                return;
            }
        }

        _notices.Publish(new KickNotice(KickNotice.Redemption, new { id = redemption.Id, status = redemption.Status, reward_id = redemption.RewardId, reward_title = redemption.RewardTitle, cost = redemption.Cost, username = redemption.RedeemerUsername, user_input = redemption.UserInput }, now));
        Log.Redemption(_log, redemption.RewardTitle);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Kick: stream session {Id} opened (started {StartedAt:u})")]
        public static partial void SessionOpened(ILogger logger, long id, DateTimeOffset startedAt);

        [LoggerMessage(Level = LogLevel.Information, Message = "Kick: stream session {Id} closed ({Wins}-{Losses})")]
        public static partial void SessionClosed(ILogger logger, long id, int wins, int losses);

        [LoggerMessage(Level = LogLevel.Information, Message = "Kick: end-of-stream summary posted for session {Id}")]
        public static partial void SummaryPosted(ILogger logger, long id);

        [LoggerMessage(Level = LogLevel.Information, Message = "Kick: prediction reminder ({Result})")]
        public static partial void Reminder(ILogger logger, string result);

        [LoggerMessage(Level = LogLevel.Information, Message = "Kick: redemption shown ({Reward})")]
        public static partial void Redemption(ILogger logger, string reward);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Kick: stream session update failed")]
        public static partial void SessionFailed(ILogger logger, Exception exception);
    }
}
