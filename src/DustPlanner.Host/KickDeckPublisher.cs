using DustPlanner.App;
using DustPlanner.Core.Planner;
using DustPlanner.Kick;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Host;

/// <summary>
/// Stage K6: keeps the relay's copy of the active deck current while the public page is on (PRD §5.5). Sends
/// <c>deck.publish</c> when the active deck changes, when the connection comes up, and when the page is switched on from
/// Settings. Off = nothing is sent, and the relay deletes what it had when the config says so. Only name, deckstring,
/// class and format travel: no card data.
/// </summary>
internal sealed partial class KickDeckPublisher : BackgroundService
{
    private readonly OverlayStateService _overlay;
    private readonly KickConnector _connector;
    private readonly AppServices _services;
    private readonly ILogger<KickDeckPublisher> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastPublished;

    public KickDeckPublisher(OverlayStateService overlay, KickConnector connector, AppServices services, ILogger<KickDeckPublisher> log)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The address viewers get in <c>!deck</c>: only while connected and the page is on.</summary>
    public static string? PublicDeckUrl(KickConnector connector, KickPrefs kick)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(kick);
        var state = connector.Current;
        return kick.PublicPage && state.Status == KickStatus.Connected && state.Channel is { Slug.Length: > 0 } channel
            ? connector.RelayUrl + "/d/" + Uri.EscapeDataString(channel.Slug)
            : null;
    }

    /// <summary>Called after the preferences were saved: a page just switched on gets the current deck at once.</summary>
    public Task RepublishAsync(CancellationToken ct = default) => PublishAsync(force: true, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnConnection(KickConnectionState state)
        {
            if (state.Status == KickStatus.Connected)
            {
                _ = PublishAsync(force: true, stoppingToken);
            }
        }

        _connector.StateChanged += OnConnection;
        try
        {
            await foreach (var state in _overlay.Subscribe(stoppingToken).ConfigureAwait(false))
            {
                var key = Key(state.ActiveDeck);
                if (key is not null && key != _lastPublished)
                {
                    await PublishAsync(force: false, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            _connector.StateChanged -= OnConnection;
        }
    }

    private async Task PublishAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            KickPrefs kick;
            try
            {
                kick = _services.ReadPrefs().Kick ?? new KickPrefs();
                kick.Validate();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is IOException)
            {
                return; // a broken prefs.json means "off", like every other default
            }

            var deck = _overlay.Current.ActiveDeck;
            if (!kick.PublicPage || deck is null || string.IsNullOrEmpty(deck.Deckstring) || _connector.Current.Status != KickStatus.Connected)
            {
                return;
            }

            var key = Key(deck);
            if (!force && key == _lastPublished)
            {
                return;
            }

            if (await _connector.PublishDeckAsync(deck.Name, deck.Deckstring, deck.CardClass, deck.Format, ct).ConfigureAwait(false))
            {
                _lastPublished = key;
                Log.Published(_log, deck.Name);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Key(ActiveDeckState? deck) => deck is null ? null : deck.Name + "\n" + deck.Deckstring + "\n" + deck.Format;

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Kick: published deck \"{Name}\" to the public page")]
        public static partial void Published(ILogger logger, string name);
    }
}
