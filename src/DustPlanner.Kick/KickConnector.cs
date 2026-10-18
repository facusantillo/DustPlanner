using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DustPlanner.Core.Planner;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Kick;

public enum KickStatus
{
    /// <summary>No pairing stored.</summary>
    Disconnected,

    /// <summary>"Connect with Kick" was pressed; waiting for the browser to come back with the code.</summary>
    Pairing,

    /// <summary>A pairing exists; the socket is not up (yet, or again).</summary>
    Connecting,

    Connected,

    /// <summary>The relay says the authorisation is gone or short of scopes: the streamer has to press Connect again.</summary>
    NeedsReconnect,
}

/// <summary>Everything the panel shows about the connection. Immutable; a new instance is published on every change.</summary>
public sealed record KickConnectionState
{
    public KickStatus Status { get; init; }

    public RelayChannel? Channel { get; init; }

    public IReadOnlyList<string> MissingScopes { get; init; } = Array.Empty<string>();

    public LiveStatus? Live { get; init; }

    /// <summary>Last problem worth showing (connection refused, relay error); cleared on the next hello.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }

    /// <summary>Until when the pairing started from the panel accepts a code.</summary>
    public DateTimeOffset? PairingExpiresAt { get; init; }

    /// <summary>The last command the relay forwarded (what the viewer typed), so the panel can show the connection works.</summary>
    public string? LastCommand { get; init; }

    public DateTimeOffset? LastCommandAt { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    public string RelayVersion { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record KickConnectorOptions
{
    public const string DefaultRelayUrl = "https://live.dustplanner.app";

    /// <summary>Overridden with <c>--relay</c> or <c>DUSTPLANNER_RELAY_URL</c> (tests point it at an in-process fake).</summary>
    public string RelayUrl { get; init; } = DefaultRelayUrl;

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PairingWindow { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The service's side of the relay (PRD §5.4): pairs through the browser, keeps one outbound WebSocket to the relay
/// with reconnection, publishes its state, hands commands to whoever listens and sends chat lines back. Opens no port:
/// the host keeps listening on loopback only.
/// </summary>
public sealed class KickConnector : BackgroundService
{
    private readonly KickConnectorOptions _options;
    private readonly IDeviceTokenStore _store;
    private readonly Func<(ChatPrefs Chat, KickPrefs Kick)> _prefs;
    private readonly HttpClient _http;
    private readonly ILogger<KickConnector> _log;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Random _random = new();

    private KickConnectionState _state;
    private KickPairing? _pairing;
    private (string Verifier, string Pair, DateTimeOffset ExpiresAt)? _pending;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCts;
    private DateTimeOffset _lastPong;

    public KickConnector(KickConnectorOptions options, IDeviceTokenStore store, Func<(ChatPrefs Chat, KickPrefs Kick)> prefs, ILogger<KickConnector> log, HttpClient? http = null, TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _clock = clock ?? TimeProvider.System;
        _pairing = _store.Load();
        _state = new KickConnectionState
        {
            Status = _pairing is null ? KickStatus.Disconnected : KickStatus.Connecting,
            Channel = _pairing?.Channel,
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    public KickConnectionState Current => _state;

    /// <summary>Raised on every state change (the host streams it on <c>/events</c>).</summary>
    public event Action<KickConnectionState>? StateChanged;

    /// <summary>A command a viewer typed, already filtered by the relay. Stage K5 answers it; K4 only shows it.</summary>
    public event Action<ChatCommandMessage>? CommandReceived;

    public event Action<StreamStatusMessage>? StreamStatusChanged;

    public event Action<RewardRedemptionMessage>? RedemptionReceived;

    public string RelayUrl => _options.RelayUrl.TrimEnd('/');

    // ------------------------------------------------------------------------------------------------ pairing

    /// <summary>Step 1 of PRD §5.4: a fresh verifier, its hash in the URL the browser must open. Valid for ten minutes.</summary>
    public string BeginPairing(int servicePort)
    {
        var verifier = RandomBase64Url(32);
        var pair = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var expires = _clock.GetUtcNow() + _options.PairingWindow;
        lock (_gate)
        {
            _pending = (verifier, pair, expires);
        }

        Publish(s => s with { Status = _pairing is null ? KickStatus.Pairing : s.Status, PairingExpiresAt = expires, LastError = null });
        return $"{RelayUrl}/connect?pair={pair}&port={servicePort}";
    }

    public bool HasPendingPairing => _pending is { } p && p.ExpiresAt > _clock.GetUtcNow();

    /// <summary>Step 2: the code the browser brought back (or the one pasted by hand) is exchanged for a device token.</summary>
    public async Task<KickPairing> CompletePairingAsync(string code, CancellationToken ct = default)
    {
        (string Verifier, string Pair, DateTimeOffset ExpiresAt) pending;
        lock (_gate)
        {
            if (_pending is not { } p || p.ExpiresAt <= _clock.GetUtcNow())
            {
                throw new KickPairingException("No pairing in progress: press Connect with Kick first (the code is only accepted for ten minutes).");
            }

            pending = p;
        }

        code = (code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            throw new KickPairingException("The code is empty.");
        }

        using var response = await _http.PostAsJsonAsync($"{RelayUrl}/pair/exchange", new { code, pair_verifier = pending.Verifier }, RelayProtocol.Json, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            throw new KickPairingException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound => "The code expired or was already used: press Connect with Kick again.",
                System.Net.HttpStatusCode.Forbidden => "The code belongs to another pairing attempt: press Connect with Kick again and use the new code.",
                _ => $"The relay refused the pairing ({(int)response.StatusCode}{(detail is null ? string.Empty : ": " + detail)}).",
            });
        }

        var body = await response.Content.ReadFromJsonAsync<PairExchangeResponse>(RelayProtocol.Json, ct).ConfigureAwait(false);
        if (body?.DeviceToken is null || body.Channel is null)
        {
            throw new KickPairingException("The relay answered without a device token.");
        }

        var pairing = new KickPairing(body.DeviceToken, body.Channel, RelayUrl, _clock.GetUtcNow());
        _store.Save(pairing);
        lock (_gate)
        {
            _pairing = pairing;
            _pending = null;
        }

        Publish(s => s with { Status = KickStatus.Connecting, Channel = pairing.Channel, PairingExpiresAt = null, MissingScopes = Array.Empty<string>(), LastError = null });
        Log.Paired(_log, pairing.Channel.Slug);
        await RestartSessionAsync().ConfigureAwait(false);
        return pairing;
    }

    /// <summary>Tells the relay to forget the channel (subscriptions, tokens, everything) and forgets the device token here.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var pairing = _pairing;
        if (pairing is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{RelayUrl}/disconnect");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairing.DeviceToken);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.DisconnectFailed(_log, (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                Log.DisconnectFailed(_log, 0);
            }
        }

        Forget(KickStatus.Disconnected, null);
        await RestartSessionAsync().ConfigureAwait(false);
    }

    /// <summary>Re-sends the config after the preferences changed (the relay replicates it to its KV filter).</summary>
    public async Task RefreshConfigAsync(CancellationToken ct = default)
    {
        if (_state.Status == KickStatus.Connected)
        {
            await SendConfigAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>A chat line to post (stage K5 calls this from the responder). False when not connected.</summary>
    public async Task<bool> SendChatAsync(string text, string? replyToMessageId, CancellationToken ct = default) =>
        await SendAsync(RelayProtocol.ChatSendMessage(text, replyToMessageId), ct).ConfigureAwait(false);

    public async Task<bool> PublishDeckAsync(string name, string deckstring, string? cardClass, string? format, CancellationToken ct = default) =>
        await SendAsync(RelayProtocol.DeckPublishMessage(name, deckstring, cardClass, format), ct).ConfigureAwait(false);

    // ------------------------------------------------------------------------------------------------ connection loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = _options.MinBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            var pairing = _pairing;
            if (pairing is null || _state.Status == KickStatus.NeedsReconnect)
            {
                // Nothing to connect with: sleep until a pairing (or re-pairing) wakes us.
                await _wake.WaitAsync(stoppingToken).ConfigureAwait(false);
                backoff = _options.MinBackoff;
                continue;
            }

            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _sessionCts = session;
            var outcome = await RunSessionAsync(pairing, session.Token).ConfigureAwait(false);
            _sessionCts = null;
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            switch (outcome)
            {
                case SessionOutcome.Replaced:
                    backoff = _options.MinBackoff;
                    continue; // pairing changed under us: connect again right away
                case SessionOutcome.Stop:
                    continue; // state already says why; wait for a wake-up
                case SessionOutcome.Healthy:
                    backoff = _options.MinBackoff;
                    break;
                case SessionOutcome.Failed:
                    backoff = TimeSpan.FromMilliseconds(Math.Min(_options.MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
                    break;
            }

            var delay = Jitter(backoff);
            Publish(s => s with { Status = s.Status == KickStatus.NeedsReconnect ? s.Status : KickStatus.Connecting, NextRetryAt = _clock.GetUtcNow() + delay });
            try
            {
                await Task.WhenAny(Task.Delay(delay, stoppingToken), _wake.WaitAsync(stoppingToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private enum SessionOutcome
    {
        Healthy,
        Failed,
        Stop,
        Replaced,
    }

    private async Task<SessionOutcome> RunSessionAsync(KickPairing pairing, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + pairing.DeviceToken);
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // we ping at the application level, the relay answers without waking
        var url = new UriBuilder(RelayUrl + "/ws");
        url.Scheme = url.Scheme == "https" ? "wss" : "ws";
        var sawHello = false;
        try
        {
            await socket.ConnectAsync(url.Uri, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            socket.Dispose();
            if (ex.Message.Contains("401", StringComparison.Ordinal) || ex.WebSocketErrorCode == WebSocketError.NotAWebSocket && ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                // The relay no longer knows this device (disconnected elsewhere, channel deleted): pairing again is the only way.
                Forget(KickStatus.NeedsReconnect, "The relay does not recognise this device any more; connect again from Settings.");
                return SessionOutcome.Stop;
            }

            Log.ConnectFailed(_log, ex.Message);
            Publish(s => s with { LastError = "Cannot reach the relay: " + ex.Message });
            return SessionOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            socket.Dispose();
            Log.ConnectFailed(_log, ex.Message);
            Publish(s => s with { LastError = "Cannot reach the relay: " + ex.Message });
            return SessionOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            return SessionOutcome.Replaced;
        }

        _socket = socket;
        _lastPong = _clock.GetUtcNow();
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = PingLoopAsync(socket, pingCts.Token);
        try
        {
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (WebSocketException ex)
                {
                    Log.SocketDropped(_log, ex.Message);
                    Publish(s => s with { LastError = "Connection lost: " + ex.Message });
                    return sawHello ? SessionOutcome.Healthy : SessionOutcome.Failed;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatus == (WebSocketCloseStatus)4000)
                    {
                        // The relay says the channel was disconnected (from another device or by the alarm).
                        Forget(KickStatus.Disconnected, null);
                        return SessionOutcome.Stop;
                    }

                    Publish(s => s with { LastError = "The relay closed the connection" + (string.IsNullOrEmpty(result.CloseStatusDescription) ? string.Empty : ": " + result.CloseStatusDescription) });
                    return sawHello ? SessionOutcome.Healthy : SessionOutcome.Failed;
                }

                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = text.ToString();
                text.Clear();
                if (RelayProtocol.Parse(frame) is not { } message)
                {
                    continue;
                }

                if (message is HelloMessage hello)
                {
                    sawHello = true;
                    await OnHelloAsync(hello, ct).ConfigureAwait(false);
                    if (hello.NeedsReconnect)
                    {
                        return SessionOutcome.Stop;
                    }

                    continue;
                }

                if (!Handle(message))
                {
                    return SessionOutcome.Stop;
                }
            }

            return sawHello ? SessionOutcome.Healthy : SessionOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested && _pairing != pairing ? SessionOutcome.Replaced : SessionOutcome.Healthy;
        }
        finally
        {
            await pingCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pinger.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            _socket = null;
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // closing is best effort
            }

            socket.Dispose();
            if (_state.Status == KickStatus.Connected)
            {
                Publish(s => s with { Status = KickStatus.Connecting, ConnectedAt = null });
            }
        }
    }

    private async Task OnHelloAsync(HelloMessage hello, CancellationToken ct)
    {
        Log.Connected(_log, hello.Channel.Slug, hello.ServerVersion);
        if (hello.NeedsReconnect)
        {
            Publish(s => s with
            {
                Status = KickStatus.NeedsReconnect,
                Channel = hello.Channel,
                MissingScopes = hello.MissingScopes,
                Live = hello.Live,
                RelayVersion = hello.ServerVersion,
                LastError = hello.MissingScopes.Count > 0 ? "Kick granted fewer permissions than needed: connect again." : "The Kick authorisation is no longer valid: connect again.",
                ConnectedAt = null,
                NextRetryAt = null,
            });
            return;
        }

        Publish(s => s with
        {
            Status = KickStatus.Connected,
            Channel = hello.Channel,
            MissingScopes = Array.Empty<string>(),
            Live = hello.Live,
            RelayVersion = hello.ServerVersion,
            LastError = null,
            ConnectedAt = _clock.GetUtcNow(),
            NextRetryAt = null,
        });
        await SendConfigAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns false when the session must stop (the relay asked for a new authorisation).</summary>
    private bool Handle(RelayMessage message)
    {
        switch (message)
        {
            case PongMessage:
                _lastPong = _clock.GetUtcNow();
                return true;
            case ChatCommandMessage command:
                // Listeners first, state second: whoever waits for the state can rely on the command having been handed over.
                CommandReceived?.Invoke(command);
                Publish(s => s with { LastCommand = (command.Command + " " + command.Args).Trim(), LastCommandAt = command.At ?? _clock.GetUtcNow() });
                return true;
            case StreamStatusMessage status:
                StreamStatusChanged?.Invoke(status);
                Publish(s => s with { Live = status.Live });
                return true;
            case RewardRedemptionMessage redemption:
                RedemptionReceived?.Invoke(redemption);
                return true;
            case RelayErrorMessage error:
                if (error.Code == "needs_reconnect")
                {
                    Publish(s => s with { Status = KickStatus.NeedsReconnect, LastError = "The Kick authorisation is no longer valid: connect again." + (error.Detail.Length > 0 ? " (" + error.Detail + ")" : string.Empty), ConnectedAt = null });
                    return false;
                }

                Log.RelayError(_log, error.Code);
                Publish(s => s with { LastError = error.Code + (error.Detail.Length > 0 ? ": " + error.Detail : string.Empty) });
                return true;
            default:
                return true;
        }
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.PingInterval, ct).ConfigureAwait(false);
            if (_clock.GetUtcNow() - _lastPong > _options.PingInterval * 2.5)
            {
                // Two pings unanswered: the connection is dead even if the socket does not know yet.
                Log.PingTimeout(_log);
                socket.Abort();
                return;
            }

            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(RelayProtocol.PingFrame), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                return;
            }
        }
    }

    private async Task SendConfigAsync(CancellationToken ct)
    {
        var (chat, kick) = _prefs();
        await SendAsync(RelayProtocol.ConfigMessage(chat, kick), ct).ConfigureAwait(false);
    }

    private async Task<bool> SendAsync(JsonObject message, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return false;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(RelayProtocol.Json));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private void Forget(KickStatus status, string? error)
    {
        _store.Delete();
        lock (_gate)
        {
            _pairing = null;
        }

        Publish(s => s with { Status = status, Channel = status == KickStatus.Disconnected ? null : s.Channel, LastError = error, ConnectedAt = null, NextRetryAt = null, Live = null, LastCommand = null, LastCommandAt = null });
    }

    private async Task RestartSessionAsync()
    {
        var current = _sessionCts;
        if (current is not null)
        {
            try
            {
                await current.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // the loop already moved on
            }
        }

        _wake.Release();
    }

    private void Publish(Func<KickConnectionState, KickConnectionState> change)
    {
        KickConnectionState next;
        lock (_gate)
        {
            next = change(_state) with { UpdatedAt = _clock.GetUtcNow() };
            _state = next;
        }

        StateChanged?.Invoke(next);
    }

    private TimeSpan Jitter(TimeSpan value)
    {
        double factor;
        lock (_random)
        {
            factor = 0.8 + _random.NextDouble() * 0.4;
        }

        return TimeSpan.FromMilliseconds(value.TotalMilliseconds * factor);
    }

    private static string RandomBase64Url(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(RelayProtocol.Json, ct).ConfigureAwait(false);
            return (string?)body?["detail"] ?? (string?)body?["error"];
        }
        catch (Exception ex) when (ex is JsonException || ex is HttpRequestException)
        {
            return null;
        }
    }

    public override void Dispose()
    {
        _socket?.Dispose();
        _wake.Dispose();
        base.Dispose();
    }

    private sealed class PairExchangeResponse
    {
        public string? DeviceToken { get; set; }

        public RelayChannel? Channel { get; set; }
    }
}

public sealed class KickPairingException : Exception
{
    public KickPairingException(string message)
        : base(message)
    {
    }

    public KickPairingException()
    {
    }

    public KickPairingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Kick: paired with @{Slug}")]
    public static partial void Paired(ILogger logger, string slug);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kick: connected as @{Slug} (relay {Version})")]
    public static partial void Connected(ILogger logger, string slug, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kick: cannot connect to the relay: {Reason}")]
    public static partial void ConnectFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kick: connection dropped: {Reason}")]
    public static partial void SocketDropped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kick: no pong from the relay; reconnecting")]
    public static partial void PingTimeout(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kick: relay reported {Code}")]
    public static partial void RelayError(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kick: /disconnect answered {Status} (0 = unreachable); the local pairing was removed anyway")]
    public static partial void DisconnectFailed(ILogger logger, int status);
}
