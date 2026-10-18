using System.Text.Json.Nodes;
using DustPlanner.Core.Planner;
using DustPlanner.Kick;
using Microsoft.Extensions.Logging.Abstractions;

namespace DustPlanner.Kick.Tests;

/// <summary>Stage K5: a command from the relay comes back as <c>chat.send</c> through the responder, cooldowns included.</summary>
#pragma warning disable CA1001 // the relay is disposed in DisposeAsync (xunit's lifetime), which the analyser cannot see
public sealed class KickChatBridgeTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static readonly ChatContext Context = new()
    {
        Deck = new ChatDeck("Priest Control", "AAECAa0GAA==", "PRIEST", "standard"),
        Today = new ChatRecord(3, 1, 0),
    };

    private readonly string _home = Path.Combine(Path.GetTempPath(), "dustplanner-kick-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelay _relay = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly ChatPrefs _chat = new() { Language = "es" };
    private readonly ChatResponder _responder = new();
    private KickConnector? _connector;
    private KickChatBridge? _bridge;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        await _relay.StartAsync();
        Assert.Empty(_chat.Validate());
    }

    public async Task DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.StopAsync(CancellationToken.None);
        }

        if (_connector is not null)
        {
            await _connector.StopAsync(CancellationToken.None);
            _connector.Dispose();
        }

        await _relay.DisposeAsync();
        TempDir.Delete(_home);
    }

    [Fact]
    public async Task A_command_is_answered_as_a_reply_through_the_relay_and_the_outcome_is_published()
    {
        await ConnectAsync();
        await _relay.PushAsync(Command("m-1", "deck", "ana"));

        var sent = await _relay.WaitForMessageAsync("chat.send");
        Assert.Equal("Priest Control · AAECAa0GAA== · copialo y pegalo en Hearthstone", (string?)sent["text"]);
        Assert.Equal("m-1", (string?)sent["reply_to_message_id"]);
        await WaitForOutcomeAsync(CommandOutcomes.Answered);
    }

    [Fact]
    public async Task Cooldowns_are_shared_with_the_local_endpoint_so_a_second_bot_cannot_double_the_answer()
    {
        await ConnectAsync();
        // Streamer.bot (the local endpoint) answered !deck a moment ago through the same responder.
        Assert.Equal(ChatReplyStatus.Ok, _responder.Respond(_chat, "deck", "ana", null, Context, _clock.GetUtcNow()).Status);

        await _relay.PushAsync(Command("m-2", "deck", "bruno"));
        await WaitForOutcomeAsync(CommandOutcomes.Cooldown);
        Assert.DoesNotContain(_relay.Received, m => (string?)m["type"] == "chat.send");

        _clock.Advance(TimeSpan.FromSeconds(_chat.CooldownSeconds + 1));
        await _relay.PushAsync(Command("m-3", "deck", "bruno"));
        var sent = await _relay.WaitForMessageAsync("chat.send");
        Assert.Equal("m-3", (string?)sent["reply_to_message_id"]);
    }

    [Fact]
    public async Task A_command_turned_off_in_the_settings_is_ignored_even_if_the_relay_forwards_it()
    {
        _chat.Commands[CommandNames.Dust].Enabled = false;
        await ConnectAsync();
        await _relay.PushAsync(Command("m-4", "dust", "ana"));
        await WaitForOutcomeAsync(CommandOutcomes.Ignored);
        Assert.DoesNotContain(_relay.Received, m => (string?)m["type"] == "chat.send");
    }

    [Fact]
    public async Task Missing_data_gets_the_friendly_line_not_an_error()
    {
        await ConnectAsync();
        await _relay.PushAsync(Command("m-5", "rank", "ana"));
        var sent = await _relay.WaitForMessageAsync("chat.send");
        Assert.Equal("Todavía no hay rango exportado", (string?)sent["text"]);
    }

    [Fact]
    public async Task Hello_subscriptions_and_later_announcements_reach_the_state()
    {
        _relay.HelloSubscriptions = Array.Empty<string>();
        await ConnectAsync();
        Assert.True(KickConnector.ChatSubscriptionMissing(_connector!.Current));

        await _relay.PushAsync(new { v = 1, type = "subscriptions", events = new[] { "chat.message.sent", "livestream.status.updated" } });
        await WaitForAsync(s => s.Subscriptions is { Count: 2 }, "subscriptions announced");
        Assert.False(KickConnector.ChatSubscriptionMissing(_connector.Current));
    }

    [Fact]
    public async Task An_old_relay_without_the_field_is_not_flagged()
    {
        _relay.HelloSubscriptions = null;
        await ConnectAsync();
        Assert.Null(_connector!.Current.Subscriptions);
        Assert.False(KickConnector.ChatSubscriptionMissing(_connector.Current));
    }

    [Fact]
    public async Task Local_chat_requests_are_noted_while_connected_at_most_once_a_minute()
    {
        await ConnectAsync();
        Assert.Null(_connector!.Current.LastLocalChatRequestAt);
        _connector.NoteLocalChatRequest();
        var first = _connector.Current.LastLocalChatRequestAt;
        Assert.Equal(_clock.GetUtcNow(), first);

        _clock.Advance(TimeSpan.FromSeconds(30));
        _connector.NoteLocalChatRequest();
        Assert.Equal(first, _connector.Current.LastLocalChatRequestAt);

        _clock.Advance(TimeSpan.FromSeconds(31));
        _connector.NoteLocalChatRequest();
        Assert.Equal(_clock.GetUtcNow(), _connector.Current.LastLocalChatRequestAt);
    }

    /// <summary>Drives cooldowns and timestamps; the sockets run on real time.</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static object Command(string id, string command, string user) =>
        new { v = 1, type = "chat.command", message_id = id, command, args = "", sender = new { user_id = user, username = user, badges = Array.Empty<string>() }, at = "2026-10-18T12:00:00Z" };

    private async Task ConnectAsync()
    {
        var options = new KickConnectorOptions { RelayUrl = _relay.Url, MinBackoff = TimeSpan.FromMilliseconds(50), MaxBackoff = TimeSpan.FromMilliseconds(400), PingInterval = TimeSpan.FromMilliseconds(300) };
        _connector = new KickConnector(options, new FileDeviceTokenStore(_home), () => (_chat, new KickPrefs()), NullLogger<KickConnector>.Instance, clock: _clock);
        _bridge = new KickChatBridge(_connector, _responder, () => _chat, () => Context, NullLogger<KickChatBridge>.Instance, _clock);
        await _bridge.StartAsync(CancellationToken.None);
        await _connector.StartAsync(CancellationToken.None);

        var url = _connector.BeginPairing(8766);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        await _connector.CompletePairingAsync(_relay.IssueCode(query["pair"]!));
        await WaitForAsync(s => s.Status == KickStatus.Connected, "connected");
        await _relay.WaitForMessageAsync("config");
    }

    private Task WaitForOutcomeAsync(string outcome) => WaitForAsync(s => s.LastCommandOutcome == outcome, "outcome " + outcome);

    private async Task WaitForAsync(Func<KickConnectionState, bool> condition, string what)
    {
        var connector = _connector!;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(KickConnectionState s)
        {
            if (condition(s))
            {
                tcs.TrySetResult(true);
            }
        }

        connector.StateChanged += OnChange;
        try
        {
            if (condition(connector.Current))
            {
                return;
            }

            // Real time: the fake clock only drives cooldowns and timestamps, not the sockets.
            if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10))) != tcs.Task)
            {
                Assert.Fail($"Timed out waiting for {what}; state: {System.Text.Json.JsonSerializer.Serialize(connector.Current, RelayProtocol.Json)}");
            }
        }
        finally
        {
            connector.StateChanged -= OnChange;
        }
    }
}
