using System.Text.Json;
using System.Text.Json.Nodes;
using DustPlanner.Core.Planner;
using DustPlanner.Kick;
using Microsoft.Extensions.Logging.Abstractions;

namespace DustPlanner.Kick.Tests;

/// <summary>The connector against an in-process relay: pairing, hello, config, commands, reconnection, disconnect.</summary>
#pragma warning disable CA1001 // the relay is disposed in DisposeAsync (xunit's lifetime), which the analyser cannot see
public sealed class KickConnectorTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dustplanner-kick-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelay _relay = new();
    private readonly List<KickConnector> _connectors = new();
    private ChatPrefs _chat = new();
    private KickPrefs _kick = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        await _relay.StartAsync();
        Assert.Empty(_chat.Validate());
    }

    public async Task DisposeAsync()
    {
        foreach (var connector in _connectors)
        {
            await connector.StopAsync(CancellationToken.None);
            connector.Dispose();
        }

        await _relay.DisposeAsync();
        TempDir.Delete(_home);
    }

    private KickConnector NewConnector(bool start = true)
    {
        var options = new KickConnectorOptions { RelayUrl = _relay.Url, MinBackoff = TimeSpan.FromMilliseconds(50), MaxBackoff = TimeSpan.FromMilliseconds(400), PingInterval = TimeSpan.FromMilliseconds(300) };
        var connector = new KickConnector(options, new FileDeviceTokenStore(_home), () => (_chat, _kick), NullLogger<KickConnector>.Instance);
        _connectors.Add(connector);
        if (start)
        {
            connector.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return connector;
    }

    private static async Task WaitForAsync(KickConnector connector, Func<KickConnectionState, bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
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

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || await Task.WhenAny(tcs.Task, Task.Delay(remaining)) != tcs.Task)
            {
                Assert.Fail($"Timed out waiting for {what}; state: {JsonSerializer.Serialize(connector.Current, RelayProtocol.Json)}");
            }
        }
        finally
        {
            connector.StateChanged -= OnChange;
        }
    }

    private async Task<KickConnector> PairedAndConnectedAsync()
    {
        var connector = NewConnector();
        var url = connector.BeginPairing(8766);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        var code = _relay.IssueCode(query["pair"]!);
        await connector.CompletePairingAsync(code);
        await WaitForAsync(connector, s => s.Status == KickStatus.Connected, "connected");
        return connector;
    }

    [Fact]
    public async Task Pairs_through_the_relay_connects_and_sends_the_config()
    {
        var connector = NewConnector();
        Assert.Equal(KickStatus.Disconnected, connector.Current.Status);

        var url = connector.BeginPairing(8766);
        var uri = new Uri(url);
        Assert.Equal(_relay.Url.TrimEnd('/') + "/connect", uri.GetLeftPart(UriPartial.Path));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Matches("^[0-9a-f]{64}$", query["pair"]);
        Assert.Equal("8766", query["port"]);
        Assert.Equal(KickStatus.Pairing, connector.Current.Status);
        Assert.True(connector.HasPendingPairing);

        var code = _relay.IssueCode(query["pair"]!);
        var pairing = await connector.CompletePairingAsync(code);
        Assert.Equal("tsukuru_tv", pairing.Channel.Slug);
        Assert.StartsWith("12345.", pairing.DeviceToken, StringComparison.Ordinal);
        Assert.False(connector.HasPendingPairing);

        await WaitForAsync(connector, s => s.Status == KickStatus.Connected, "connected");
        Assert.Equal("tsukuru_tv", connector.Current.Channel!.Slug);
        Assert.Equal("fake-0.1", connector.Current.RelayVersion);
        Assert.Null(connector.Current.LastError);

        var config = await _relay.WaitForMessageAsync("config");
        Assert.Equal("!", (string?)config["prefix"]);
        var commands = config["commands"]!.AsArray().Select(c => (string?)c!["name"]).ToArray();
        Assert.Equal(new[] { "deck", "rank", "wr", "last" }, commands); // dust is off by default
        Assert.Equal("mazo", (string?)config["commands"]![0]!["aliases"]![0]);
        Assert.False((bool?)config["public_page"]);
        Assert.True((bool?)config["forward"]!["stream_status"]);
        Assert.False((bool?)config["forward"]!["redemptions"]);

        // The pairing is on disk (in clear outside Windows, DPAPI on it) and never holds a Kick token.
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, FileDeviceTokenStore.FileName)))!.AsObject();
        Assert.Equal(OperatingSystem.IsWindows() ? "dpapi" : "none", (string?)file["protection"]);
        Assert.Equal("tsukuru_tv", (string?)file["channel"]!["slug"]);
        Assert.DoesNotContain("access", file.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_saved_pairing_connects_on_start_without_the_browser()
    {
        var first = await PairedAndConnectedAsync();
        await first.StopAsync(CancellationToken.None);

        var second = NewConnector();
        Assert.Equal(KickStatus.Connecting, second.Current.Status);
        Assert.Equal("tsukuru_tv", second.Current.Channel!.Slug);
        await WaitForAsync(second, s => s.Status == KickStatus.Connected, "reconnect from disk");
        Assert.Equal(2, _relay.Connections);
    }

    [Fact]
    public async Task Reconnects_with_backoff_when_the_relay_drops_the_socket()
    {
        var connector = await PairedAndConnectedAsync();
        await _relay.CloseAllAsync(1012, "restarting");
        await WaitForAsync(connector, s => s.Status == KickStatus.Connecting, "connecting again");
        await WaitForAsync(connector, s => s.Status == KickStatus.Connected && _relay.Connections == 2, "second connection");
        Assert.Null(connector.Current.LastError);
        // Config is sent again after every hello.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_relay.Received.Count(m => (string?)m["type"] == "config") < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, _relay.Received.Count(m => (string?)m["type"] == "config"));
    }

    [Fact]
    public async Task Commands_and_status_from_the_relay_reach_the_host()
    {
        var connector = await PairedAndConnectedAsync();
        ChatCommandMessage? received = null;
        connector.CommandReceived += c => received = c;
        StreamStatusMessage? live = null;
        connector.StreamStatusChanged += s => live = s;

        await _relay.PushAsync(new { v = 1, type = "chat.command", message_id = "m-1", command = "deck", args = "por favor", sender = new { user_id = "777", username = "ana", badges = new[] { "subscriber" } }, at = "2026-10-18T20:00:00Z" });
        await WaitForAsync(connector, s => s.LastCommand == "deck por favor", "command in state");
        Assert.NotNull(received);
        Assert.Equal("ana", received.SenderUsername);
        Assert.Equal(new[] { "subscriber" }, received.Badges);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 20, 0, 0, TimeSpan.Zero), received.At);

        await _relay.PushAsync(new { v = 1, type = "stream.status", is_live = true, title = "Legend push", started_at = "2026-10-18T19:00:00Z", ended_at = (string?)null });
        await WaitForAsync(connector, s => s.Live?.IsLive == true, "live state");
        Assert.Equal("Legend push", live!.Live.Title);

        // Garbage and unknown types are ignored, the connection stays up.
        await _relay.PushAsync(new { v = 2, type = "hello" });
        await _relay.PushAsync(new { v = 1, type = "something.new" });
        await Task.Delay(200);
        Assert.Equal(KickStatus.Connected, connector.Current.Status);
    }

    [Fact]
    public async Task Chat_lines_and_deck_publishes_go_out_as_protocol_frames()
    {
        var connector = await PairedAndConnectedAsync();
        Assert.True(await connector.SendChatAsync("Priest Control · AAECAa0G", "m-1"));
        var send = await _relay.WaitForMessageAsync("chat.send");
        Assert.Equal("Priest Control · AAECAa0G", (string?)send["text"]);
        Assert.Equal("m-1", (string?)send["reply_to_message_id"]);

        Assert.True(await connector.PublishDeckAsync("Priest Control", "AAECAa0G", "PRIEST", "standard"));
        var publish = await _relay.WaitForMessageAsync("deck.publish");
        Assert.Equal("PRIEST", (string?)publish["card_class"]);

        _kick = new KickPrefs { ShowRedemptions = true, RedemptionFilter = { "reward-1" } };
        _chat.Commands["dust"].Enabled = true;
        await connector.RefreshConfigAsync();
        await Task.Delay(100);
        var config = _relay.Received.Last(m => (string?)m["type"] == "config");
        Assert.True((bool?)config["forward"]!["redemptions"]);
        Assert.Equal("reward-1", (string?)config["forward"]!["redemption_filter"]![0]);
        Assert.Contains("dust", config["commands"]!.AsArray().Select(c => (string?)c!["name"]));
    }

    [Fact]
    public async Task Needs_reconnect_in_the_hello_stops_the_retry_loop_until_paired_again()
    {
        _relay.HelloNeedsReconnect = true;
        var connector = NewConnector();
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(connector.BeginPairing(8765)).Query);
        await connector.CompletePairingAsync(_relay.IssueCode(query["pair"]!));
        await WaitForAsync(connector, s => s.Status == KickStatus.NeedsReconnect, "needs reconnect");
        Assert.Equal(new[] { "chat:write" }, connector.Current.MissingScopes);
        await Task.Delay(600);
        Assert.Equal(1, _relay.Connections); // no hammering

        // The streamer connects again: a new pairing resumes the loop.
        _relay.HelloNeedsReconnect = false;
        query = System.Web.HttpUtility.ParseQueryString(new Uri(connector.BeginPairing(8765)).Query);
        await connector.CompletePairingAsync(_relay.IssueCode(query["pair"]!));
        await WaitForAsync(connector, s => s.Status == KickStatus.Connected, "connected after re-pairing");
    }

    [Fact]
    public async Task A_relay_error_frame_asking_to_reconnect_is_honoured()
    {
        var connector = await PairedAndConnectedAsync();
        await _relay.PushAsync(new { v = 1, type = "error", code = "needs_reconnect", detail = "the authorisation is no longer valid" });
        await WaitForAsync(connector, s => s.Status == KickStatus.NeedsReconnect, "needs reconnect");
        Assert.Contains("connect again", connector.Current.LastError, StringComparison.Ordinal);
        await Task.Delay(400);
        Assert.Equal(1, _relay.Connections);

        // Other errors are shown and the connection carries on.
        var again = await PairedAndConnectedAsync();
        await _relay.PushAsync(new { v = 1, type = "error", code = "kick_rate_limited", detail = "429" });
        await WaitForAsync(again, s => s.LastError?.StartsWith("kick_rate_limited", StringComparison.Ordinal) == true, "error shown");
        Assert.Equal(KickStatus.Connected, again.Current.Status);
    }

    [Fact]
    public async Task A_relay_that_no_longer_knows_the_device_forgets_the_pairing()
    {
        var connector = await PairedAndConnectedAsync();
        _relay.RejectSockets = true;
        await _relay.CloseAllAsync(1012, "restarting");
        await WaitForAsync(connector, s => s.Status == KickStatus.NeedsReconnect, "needs reconnect after 401");
        Assert.False(File.Exists(Path.Combine(_home, FileDeviceTokenStore.FileName)));
    }

    [Fact]
    public async Task Close_4000_means_disconnected_elsewhere()
    {
        var connector = await PairedAndConnectedAsync();
        await _relay.CloseAllAsync(4000, "disconnected");
        await WaitForAsync(connector, s => s.Status == KickStatus.Disconnected, "disconnected");
        Assert.Null(connector.Current.Channel);
        Assert.False(File.Exists(Path.Combine(_home, FileDeviceTokenStore.FileName)));
        await Task.Delay(300);
        Assert.Equal(1, _relay.Connections);
    }

    [Fact]
    public async Task Disconnect_tells_the_relay_and_clears_everything()
    {
        var connector = await PairedAndConnectedAsync();
        await connector.DisconnectAsync();
        Assert.Equal(1, _relay.DisconnectCalls);
        Assert.Equal(KickStatus.Disconnected, connector.Current.Status);
        Assert.Null(connector.Current.Channel);
        Assert.False(File.Exists(Path.Combine(_home, FileDeviceTokenStore.FileName)));
        Assert.False(await connector.SendChatAsync("nope", null));
        await Task.Delay(300);
        Assert.Equal(1, _relay.Connections);
    }

    [Fact]
    public async Task Pairing_is_refused_without_a_pending_flow_or_with_a_bad_code()
    {
        var connector = NewConnector(start: false);
        var ex = await Assert.ThrowsAsync<KickPairingException>(() => connector.CompletePairingAsync("whatever"));
        Assert.Contains("Connect with Kick first", ex.Message, StringComparison.Ordinal);

        connector.BeginPairing(8765);
        ex = await Assert.ThrowsAsync<KickPairingException>(() => connector.CompletePairingAsync("not-issued"));
        Assert.Contains("expired or was already used", ex.Message, StringComparison.Ordinal);
        ex = await Assert.ThrowsAsync<KickPairingException>(() => connector.CompletePairingAsync("   "));
        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);

        // A code issued for another verifier (someone else's flow) is refused and burnt by the relay.
        var foreign = _relay.IssueCode(new string('a', 64));
        ex = await Assert.ThrowsAsync<KickPairingException>(() => connector.CompletePairingAsync(foreign));
        Assert.Contains("another pairing attempt", ex.Message, StringComparison.Ordinal);
        Assert.Equal(KickStatus.Pairing, connector.Current.Status);
    }

    [Fact]
    public void Protocol_parsing_tolerates_missing_fields_and_rejects_other_versions()
    {
        Assert.Null(RelayProtocol.Parse("not json"));
        Assert.Null(RelayProtocol.Parse("{\"v\":2,\"type\":\"hello\"}"));
        Assert.Null(RelayProtocol.Parse("{\"v\":1}"));
        var hello = Assert.IsType<HelloMessage>(RelayProtocol.Parse("{\"v\":1,\"type\":\"hello\"}"));
        Assert.Equal(string.Empty, hello.Channel.Slug);
        Assert.False(hello.Live.IsLive);
        var command = Assert.IsType<ChatCommandMessage>(RelayProtocol.Parse("{\"v\":1,\"type\":\"chat.command\",\"command\":\"deck\"}"));
        Assert.Equal(string.Empty, command.Args);
        Assert.Null(command.At);
        Assert.IsType<PongMessage>(RelayProtocol.Parse("{\"v\":1,\"type\":\"pong\"}"));
        var error = Assert.IsType<RelayErrorMessage>(RelayProtocol.Parse("{\"v\":1,\"type\":\"error\",\"code\":\"send_dropped\"}"));
        Assert.Equal("send_dropped", error.Code);
        Assert.Equal("{\"v\":1,\"type\":\"ping\"}", RelayProtocol.PingFrame);
    }
}
