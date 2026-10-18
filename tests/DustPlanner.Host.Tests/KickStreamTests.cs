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

/// <summary>Stage K7 end to end: stream.status opens and closes a session, a game ending while live reminds, redemptions reach /events once, the summary goes to chat.</summary>
#pragma warning disable CA1001 // disposed in DisposeAsync
public sealed class KickStreamTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dustplanner-kickstream-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelay _relay = new();
    private string _session = string.Empty;
    private string[] _logLines = Array.Empty<string>();
    private int _firstGameEnd;
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var catalogDir = Path.Combine(_home, "catalog", "synthetic");
        Directory.CreateDirectory(catalogDir);
        File.Copy(Fixture("cards.synthetic.json"), Path.Combine(catalogDir, "cards.enUS.json"));
        File.WriteAllText(Path.Combine(_home, "catalog", "manifest.json"), $$"""{ "build": "synthetic", "locale": "enUS", "checked_at": "{{DateTimeOffset.UtcNow:O}}" }""");
        var inbox = Path.Combine(_home, "inbox", "8589934592-42");
        Directory.CreateDirectory(inbox);
        File.Copy(Fixture("collection.synthetic.json"), Path.Combine(inbox, "collection.json"));
        var rank = new RankExport
        {
            Meta = new ExportMeta { Kind = "rank", ExportedAt = new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero), Trigger = "manual", Account = new AccountRef { Hi = 8589934592, Lo = 42, BattleTag = "Synthetic#0001", Region = "EU" } },
            Formats = { ["standard"] = new RankInfo { LeagueId = 5, StarLevel = 6, Stars = 3, SeasonId = 150 } },
        };
        File.WriteAllText(Path.Combine(inbox, "rank.json"), JsonSerializer.Serialize(rank, CoreJson.Options));

        var logs = Path.Combine(_home, "Logs");
        _session = Path.Combine(logs, "Hearthstone_2026_10_15_23_50_00");
        Directory.CreateDirectory(_session);
        _logLines = File.ReadAllLines(Fixture("synthetic_edge_cases.power.log"));
        _firstGameEnd = Array.FindIndex(_logLines, l => l.Contains("tag=STATE value=COMPLETE", StringComparison.Ordinal)) + 1;
        File.WriteAllLines(Path.Combine(_session, "Power.log"), _logLines.Take(_firstGameEnd));

        await _relay.StartAsync();
        var options = new OverlayHostOptions { Home = _home, Port = 0, LogsDirectory = logs, PollInterval = TimeSpan.FromMilliseconds(200), RelayUrl = _relay.Url };
        _app = HostApp.Create(options, quiet: true);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
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
    public async Task Sessions_reminders_redemptions_and_the_summary()
    {
        // The overlay must know the account before the stream starts (the game already in the log at start is ignored by the tracker).
        await WaitUntilAsync(async () => (await Get("/api/state")).TryGetProperty("account_key", out var a) && a.ValueKind == JsonValueKind.String, "account known");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        // Pair and connect through the fake relay.
        var connect = await Post("/api/kick/connect", new { });
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(connect.GetProperty("connect_url").GetString()!).Query);
        await _http.GetAsync("/kick/paired?code=" + _relay.IssueCode(query["pair"]!));
        await WaitForEventAsync(reader, "kick", s => s.GetProperty("status").GetString() == "connected");
        await Put("/api/prefs", new { chat = new { prefix = "!" }, kick = new { post_stream_summary = true, prediction_reminder = "panel+chat", show_redemptions = true } });
        await _relay.WaitForMessageAsync("config");

        // Going live opens a session with the rank of the moment.
        // The fixture's games carry 2026-10-15 timestamps: the stream must have started before them to contain the second one.
        var startedAt = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        await _relay.PushAsync(new { v = 1, type = "stream.status", is_live = true, title = "Legend push", started_at = startedAt.ToString("O"), ended_at = (string?)null });
        var live = await WaitAsync(async () =>
        {
            var sessions = (await Get("/api/stream-sessions")).GetProperty("sessions");
            return sessions.GetArrayLength() == 1 ? sessions[0] : (JsonElement?)null;
        }, "session opened");
        Assert.True(live.GetProperty("is_live").GetBoolean());
        Assert.Equal("Legend push", live.GetProperty("title").GetString());
        Assert.Equal("tsukuru_tv", live.GetProperty("channel_slug").GetString());
        Assert.Equal("Diamond 5 (3 stars)", live.GetProperty("rank_start").GetProperty("label").GetString());
        Assert.Equal(0, live.GetProperty("record").GetProperty("games").GetInt32());

        // A redemption reaches the overlay once, even when Kick repeats it (pending → accepted).
        var redemption = new { v = 1, type = "reward.redemption", id = "r-1", status = "pending", reward = new { id = "rw-9", title = "Pick my next card", cost = 500 }, redeemer = new { username = "ana" }, user_input = "Reno" };
        await _relay.PushAsync(redemption);
        var shown = await ReadEventAsync(reader, "kick.redemption");
        Assert.Equal("Pick my next card", shown.GetProperty("payload").GetProperty("reward_title").GetString());
        Assert.Equal("ana", shown.GetProperty("payload").GetProperty("username").GetString());
        await _relay.PushAsync(redemption with { status = "accepted" });

        // The second game ends while live: a reminder for the panel and, as configured, a line in chat.
        await File.AppendAllLinesAsync(Path.Combine(_session, "Power.log"), _logLines.Skip(_firstGameEnd));
        var reminder = await ReadEventAsync(reader, "kick.reminder");
        Assert.Contains(reminder.GetProperty("payload").GetProperty("result").GetString(), new[] { "win", "loss", "tie" });
        var chatReminder = await _relay.WaitForMessageAsync("chat.send");
        Assert.StartsWith("Game over: ", (string?)chatReminder["text"], StringComparison.Ordinal);
        Assert.EndsWith("resolve the prediction!", (string?)chatReminder["text"], StringComparison.Ordinal);
        Assert.Single(_relay.Received, m => (string?)m["type"] == "chat.send"); // the duplicate redemption produced nothing

        // Stream ends: the session closes with the game inside it, and the summary goes to chat.
        await _relay.PushAsync(new { v = 1, type = "stream.status", is_live = false, title = "Legend push", started_at = startedAt.ToString("O"), ended_at = DateTimeOffset.UtcNow.ToString("O") });
        var closed = await WaitAsync(async () =>
        {
            var s = (await Get("/api/stream-sessions")).GetProperty("sessions")[0];
            return s.GetProperty("is_live").GetBoolean() ? (JsonElement?)null : s;
        }, "session closed");
        Assert.Equal(1, closed.GetProperty("record").GetProperty("games").GetInt32());
        var summary = await WaitAsync(() => _relay.Received.LastOrDefault(m => (string?)m["type"] == "chat.send" && ((string?)m["text"])!.StartsWith("Stream over: ", StringComparison.Ordinal)), "summary in chat");
        Assert.Matches(@"^Stream over: \d-\d(-\d)?( \(\d+%\))? · still at Diamond 5$", (string?)summary["text"]);
        await WaitUntilAsync(async () => (await Get("/api/stream-sessions")).GetProperty("sessions")[0].TryGetProperty("summary_posted_at", out var p) && p.ValueKind == JsonValueKind.String, "summary marked");

        // A repeated end changes nothing, and nothing is posted twice.
        await _relay.PushAsync(new { v = 1, type = "stream.status", is_live = false, title = "Legend push", started_at = startedAt.ToString("O"), ended_at = DateTimeOffset.UtcNow.ToString("O") });
        await Task.Delay(400);
        Assert.Equal(2, _relay.Received.Count(m => (string?)m["type"] == "chat.send"));
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private async Task<JsonElement> Get(string path) => JsonDocument.Parse(await _http!.GetStringAsync(path)).RootElement.Clone();

    private async Task<JsonElement> Post(string path, object body)
    {
        using var response = await _http!.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task Put(string path, object body)
    {
        using var response = await _http!.PutAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<JsonElement> WaitAsync(Func<Task<JsonElement?>> probe, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var value = await probe();
            if (value is not null)
            {
                return value.Value;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Timed out waiting for " + what);
        throw new InvalidOperationException();
    }

    private static async Task<System.Text.Json.Nodes.JsonObject> WaitAsync(Func<System.Text.Json.Nodes.JsonObject?> probe, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Timed out waiting for " + what);
        throw new InvalidOperationException();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Timed out waiting for " + what);
    }

    private static async Task<JsonElement> ReadEventAsync(StreamReader reader, string wanted)
    {
        var read = Task.Run(async () =>
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
        });
        if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(20))) != read)
        {
            Assert.Fail("Timed out waiting for event " + wanted);
        }

        return await read;
    }

    private static async Task<JsonElement> WaitForEventAsync(StreamReader reader, string name, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var next = await ReadEventAsync(reader, name);
            if (condition(next))
            {
                return next;
            }
        }

        Assert.Fail("Timed out waiting for " + name);
        throw new InvalidOperationException();
    }
}
