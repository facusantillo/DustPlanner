using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Json;
using DustPlanner.Host;
using DustPlanner.Kick.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DustPlanner.Host.Tests;

/// <summary>The host's side of the Kick pairing against the in-process fake relay: endpoints, the paired page, the SSE `kick` events.</summary>
#pragma warning disable CA1001 // disposed in DisposeAsync
public sealed class KickEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dustplanner-kickhost-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelay _relay = new();
    private WebApplication? _app;
    private HttpClient? _http;
    private int _port;

    public async Task InitializeAsync()
    {
        var catalogDir = Path.Combine(_home, "catalog", "synthetic");
        Directory.CreateDirectory(catalogDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cards.synthetic.json"), Path.Combine(catalogDir, "cards.enUS.json"));
        File.WriteAllText(Path.Combine(_home, "catalog", "manifest.json"), $$"""{ "build": "synthetic", "locale": "enUS", "checked_at": "{{DateTimeOffset.UtcNow:O}}" }""");
        await _relay.StartAsync();

        var options = new OverlayHostOptions { Home = _home, Port = 0, LogsDirectory = Path.Combine(_home, "no-logs"), PollInterval = TimeSpan.FromMilliseconds(200), RelayUrl = _relay.Url };
        _app = HostApp.Create(options, quiet: true);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _port = new Uri(address).Port;
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        await _relay.DisposeAsync();
        TempDir.Delete(_home);
    }

    [Fact]
    public async Task Pairing_from_the_panel_connects_and_the_events_stream_says_so()
    {
        var status = await Get("/api/kick/status");
        Assert.Equal("disconnected", status.GetProperty("state").GetProperty("status").GetString());
        Assert.Equal(_relay.Url.TrimEnd('/'), status.GetProperty("relay_url").GetString());
        Assert.False(status.GetProperty("pairing_pending").GetBoolean());

        // A stray /kick/paired link before anyone pressed Connect is refused.
        var stray = await _http!.GetAsync("/kick/paired?code=whatever");
        Assert.Equal(HttpStatusCode.Conflict, stray.StatusCode);

        // Subscribe to the event stream first: the kick state must arrive as its own event type.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var firstKick = await ReadEventAsync(reader, "kick");
        Assert.Equal("disconnected", firstKick.GetProperty("status").GetString());

        var connect = await Post("/api/kick/connect", new { });
        var connectUrl = new Uri(connect.GetProperty("connect_url").GetString()!);
        Assert.StartsWith(_relay.Url.TrimEnd('/') + "/connect?pair=", connectUrl.ToString(), StringComparison.Ordinal);
        var query = System.Web.HttpUtility.ParseQueryString(connectUrl.Query);
        Assert.Equal(_port.ToString(System.Globalization.CultureInfo.InvariantCulture), query["port"]);
        Assert.Equal("pairing", connect.GetProperty("state").GetProperty("status").GetString());

        // The relay's callback page sends the browser here with the code.
        var code = _relay.IssueCode(query["pair"]!);
        var paired = await _http.GetAsync("/kick/paired?code=" + code);
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var html = await paired.Content.ReadAsStringAsync();
        Assert.Contains("@tsukuru_tv", html, StringComparison.Ordinal);
        Assert.Contains("/#/settings", html, StringComparison.Ordinal);

        var connected = await WaitForKickAsync(reader, s => s.GetProperty("status").GetString() == "connected");
        Assert.Equal("tsukuru_tv", connected.GetProperty("channel").GetProperty("slug").GetString());
        var config = await _relay.WaitForMessageAsync("config");
        Assert.Equal("!", (string?)config["prefix"]);

        // Saving preferences pushes a new config to the relay.
        await Put("/api/prefs", new { chat = new { prefix = "?" }, kick = new { show_redemptions = true } });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !_relay.Received.Any(m => (string?)m["prefix"] == "?"))
        {
            await Task.Delay(50);
        }

        var latest = _relay.Received.Last(m => (string?)m["type"] == "config");
        Assert.Equal("?", (string?)latest["prefix"]);
        Assert.True((bool?)latest["forward"]!["redemptions"]);

        // Stage K5: a command from the relay is answered through the host's responder and state, as a chat.send.
        await _relay.PushAsync(new { v = 1, type = "chat.command", message_id = "m-1", command = "deck", args = "", sender = new { user_id = "7", username = "ana", badges = Array.Empty<string>() }, at = "2026-10-18T12:00:00Z" });
        var answer = await _relay.WaitForMessageAsync("chat.send");
        Assert.Equal("m-1", (string?)answer["reply_to_message_id"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)answer["text"]));
        var answered = await WaitForKickAsync(reader, s => s.TryGetProperty("last_command_outcome", out var o) && o.GetString() == "answered");
        Assert.Equal("deck", answered.GetProperty("last_command").GetString());

        // Streamer.bot still polling next to the native connection: the state says so, so the panel can warn.
        using (var local = await _http.GetAsync("/api/chat/deck?user=ana"))
        {
            Assert.Equal(HttpStatusCode.NoContent, local.StatusCode); // same responder, same cooldown
        }

        var flagged = await WaitForKickAsync(reader, s => s.TryGetProperty("last_local_chat_request_at", out var at) && at.ValueKind == JsonValueKind.String);
        Assert.Equal("connected", flagged.GetProperty("status").GetString());

        // Stage K6: an active deck exported by HDT reaches the relay only once the public page is on, and !deck then links to it.
        var inbox = Path.Combine(_home, "inbox", "8589934592-42");
        Directory.CreateDirectory(inbox);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "collection.synthetic.json"), Path.Combine(inbox, ExportSchema.CollectionFileName));
        var activeDeck = new ActiveDeckExport
        {
            Meta = new ExportMeta { Kind = ExportSchema.ActiveDeckKind, ExportedAt = DateTimeOffset.UtcNow, Trigger = "manual", Account = new AccountRef { Hi = 8589934592, Lo = 42, BattleTag = "Synthetic#0001", Region = "EU" } },
            Deck = new ActiveDeck { Name = "Synthetic Priest", Deckstring = "AAECAa0GAA==", HdtDeckId = "6f0c2a1e-5b7d-4e3a-9c21-0f8d6a4b2c11", CardClass = "PRIEST", Format = "standard" },
        };
        File.WriteAllText(Path.Combine(inbox, ExportSchema.ActiveDeckFileName), JsonSerializer.Serialize(activeDeck, CoreJson.Options));
        var seen = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < seen && (await Get("/api/state")).GetProperty("active_deck").ValueKind != JsonValueKind.Object)
        {
            await Task.Delay(100);
        }

        Assert.Equal(JsonValueKind.Object, (await Get("/api/state")).GetProperty("active_deck").ValueKind);
        await Task.Delay(300);
        Assert.DoesNotContain(_relay.Received, m => (string?)m["type"] == "deck.publish"); // page still off

        await Put("/api/prefs", new { chat = new { prefix = "!" }, kick = new { public_page = true } });
        var published = await _relay.WaitForMessageAsync("deck.publish");
        Assert.Equal("Synthetic Priest", (string?)published["name"]);
        Assert.Equal("AAECAa0GAA==", (string?)published["deckstring"]);
        Assert.Equal("PRIEST", (string?)published["card_class"]);
        var pageOn = _relay.Received.Last(m => (string?)m["type"] == "config");
        Assert.True((bool?)pageOn["public_page"]);

        // Saving the preferences reset the cooldowns, so the local endpoint answers again — now with the page's address before the code.
        var withLink = await _http.GetStringAsync("/api/chat/deck?lang=es&user=bruno");
        Assert.Equal("Synthetic Priest · lista: " + _relay.Url.TrimEnd('/') + "/d/tsukuru_tv · AAECAa0GAA==", withLink);

        var disconnected = await Post("/api/kick/disconnect", new { });
        Assert.Equal("disconnected", disconnected.GetProperty("state").GetProperty("status").GetString());
        Assert.Equal(1, _relay.DisconnectCalls);
        Assert.False(File.Exists(Path.Combine(_home, "kick.json")));
    }

    [Fact]
    public async Task Manual_code_and_validation_of_the_kick_prefs()
    {
        var bad = await _http!.PostAsJsonAsync("/api/kick/pair", new { code = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Connect with Kick first", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var connect = await Post("/api/kick/connect", new { });
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(connect.GetProperty("connect_url").GetString()!).Query);
        var manual = await Post("/api/kick/pair", new { code = _relay.IssueCode(query["pair"]!) });
        Assert.Contains(manual.GetProperty("state").GetProperty("status").GetString(), new[] { "connecting", "connected" });

        var badPrefs = await _http!.PutAsJsonAsync("/api/prefs", new { kick = new { prediction_reminder = "loud" } });
        Assert.Equal(HttpStatusCode.BadRequest, badPrefs.StatusCode);
        Assert.Contains("kick.prediction_reminder", await badPrefs.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var saved = await Put("/api/prefs", new { kick = new { prediction_reminder = "PANEL+CHAT", redemption_filter = new[] { " r1 ", "", "r1" } } });
        var kick = saved.GetProperty("prefs").GetProperty("kick");
        Assert.Equal("panel+chat", kick.GetProperty("prediction_reminder").GetString());
        Assert.Equal(new[] { "r1" }, kick.GetProperty("redemption_filter").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(kick.GetProperty("public_page").GetBoolean());
    }

    private static async Task<JsonElement> ReadEventAsync(StreamReader reader, string wanted)
    {
        string? eventName = null;
        while (true)
        {
            var line = await reader.ReadLineAsync() ?? throw new EndOfStreamException("event stream ended");
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventName == wanted)
            {
                return JsonDocument.Parse(line[6..]).RootElement.Clone();
            }
        }
    }

    private static async Task<JsonElement> WaitForKickAsync(StreamReader reader, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            var read = ReadEventAsync(reader, "kick");
            if (await Task.WhenAny(read, Task.Delay(deadline - DateTime.UtcNow)) != read)
            {
                break;
            }

            last = await read;
            if (condition(last))
            {
                return last;
            }
        }

        Assert.Fail("Timed out waiting for the kick state; last: " + last);
        return last;
    }

    private async Task<JsonElement> Get(string path) => JsonDocument.Parse(await _http!.GetStringAsync(path)).RootElement.Clone();

    private async Task<JsonElement> Post(string path, object body)
    {
        using var response = await _http!.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> Put(string path, object body)
    {
        using var response = await _http!.PutAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
