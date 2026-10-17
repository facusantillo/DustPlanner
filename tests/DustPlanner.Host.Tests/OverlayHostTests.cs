using System.Net;
using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Json;
using DustPlanner.Host;
using DustPlanner.LogTracker;
using DustPlanner.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DustPlanner.Host.Tests;

/// <summary>Runs the real host in-process on a random loopback port against a synthetic home and logs folder.</summary>
public sealed class OverlayHostTests : IAsyncLifetime, IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dustplanner-host-" + Guid.NewGuid().ToString("N"));
    private string _logs = string.Empty;
    private string _session = string.Empty;
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var catalogDir = Path.Combine(_home, "catalog", "synthetic");
        Directory.CreateDirectory(catalogDir);
        File.Copy(Fixture("cards.synthetic.json"), Path.Combine(catalogDir, "cards.enUS.json"));
        File.WriteAllText(Path.Combine(_home, "catalog", "manifest.json"),
            $$"""{ "build": "synthetic", "locale": "enUS", "checked_at": "{{DateTimeOffset.UtcNow:O}}" }""");
        var inbox = Path.Combine(_home, "inbox", "8589934592-42");
        Directory.CreateDirectory(inbox);
        File.Copy(Fixture("collection.synthetic.json"), Path.Combine(inbox, "collection.json"));
        var rank = new RankExport
        {
            Meta = new ExportMeta { Kind = "rank", ExportedAt = new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero), Trigger = "manual", Account = new AccountRef { Hi = 8589934592, Lo = 42, BattleTag = "Synthetic#0001", Region = "EU" } },
            Formats = { ["standard"] = new RankInfo { LeagueId = 5, StarLevel = 6, Stars = 3, SeasonId = 150 }, ["wild"] = new RankInfo { LeagueId = 3, StarLevel = 2, Stars = 1, SeasonId = 150 } },
        };
        File.WriteAllText(Path.Combine(inbox, "rank.json"), JsonSerializer.Serialize(rank, CoreJson.Options));
        File.WriteAllText(Path.Combine(inbox, ExportSchema.ActiveDeckFileName), JsonSerializer.Serialize(ActiveDeck(new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero), "manual"), CoreJson.Options));

        _logs = Path.Combine(_home, "Logs");
        _session = Path.Combine(_logs, "Hearthstone_2026_10_15_23_50_00");
        Directory.CreateDirectory(_session);
        // The first game is already in the log when the host starts: it must not count towards the session.
        var lines = File.ReadAllLines(Fixture("synthetic_edge_cases.power.log"));
        var firstGameEnd = Array.FindIndex(lines, l => l.Contains("tag=STATE value=COMPLETE", StringComparison.Ordinal)) + 1;
        File.WriteAllLines(Path.Combine(_session, "Power.log"), lines.Take(firstGameEnd));

        var options = new OverlayHostOptions { Home = _home, Port = 0, LogsDirectory = _logs, PollInterval = TimeSpan.FromMilliseconds(200) };
        _app = HostApp.Create(options, quiet: true);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    private static ActiveDeckExport ActiveDeck(DateTimeOffset at, string trigger) => new()
    {
        Meta = new ExportMeta { Kind = ExportSchema.ActiveDeckKind, ExportedAt = at, Trigger = trigger, Account = new AccountRef { Hi = 8589934592, Lo = 42, BattleTag = "Synthetic#0001", Region = "EU" } },
        Deck = new Contracts.ActiveDeck { Name = "Synthetic Priest", Deckstring = "AAECAa0GAA==", HdtDeckId = "6f0c2a1e-5b7d-4e3a-9c21-0f8d6a4b2c11", CardClass = "PRIEST", Format = "standard" },
    };

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        TempDir.Delete(_home);
    }

    [Fact]
    public async Task Pages_and_state_are_served_from_loopback()
    {
        var overlay = await _http!.GetStringAsync("/overlay");
        Assert.Contains("background: transparent", overlay, StringComparison.Ordinal);
        Assert.Contains("width: 1920px; height: 1080px", overlay, StringComparison.Ordinal);
        Assert.Contains("EventSource(\"/events\")", overlay, StringComparison.Ordinal);
        Assert.Contains("id=\"cell-deck\"", overlay, StringComparison.Ordinal);

        var panel = await _http.GetStringAsync("/");
        Assert.Contains("/panel/app.js", panel, StringComparison.Ordinal);
        var status = await _http.GetStringAsync("/status");
        Assert.Contains("OBS setup", status, StringComparison.Ordinal);

        var state = await State();
        Assert.True(state.GetProperty("account_key").GetString() == "8589934592-42", "unexpected state: " + state);
        Assert.Equal("Synthetic#0001", state.GetProperty("battle_tag").GetString());
        Assert.Equal(640, state.GetProperty("dust").GetProperty("value").GetInt32());
        Assert.Equal("Diamond 5 (3 stars)", state.GetProperty("rank").GetProperty("label").GetString());
        Assert.Equal("standard", state.GetProperty("rank").GetProperty("format").GetString());
        Assert.Empty(state.GetProperty("warnings").EnumerateArray());
        Assert.Equal(0, state.GetProperty("session").GetProperty("games").GetInt32());
        Assert.Equal("Synthetic Priest", state.GetProperty("active_deck").GetProperty("name").GetString());
        Assert.Equal("PRIEST", state.GetProperty("active_deck").GetProperty("card_class").GetString());
        Assert.Equal("AAECAa0GAA==", state.GetProperty("active_deck").GetProperty("deckstring").GetString());

        var missing = await _http.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Games_completed_after_start_update_session_today_and_events()
    {
        // Subscribe to the event stream first, then append the rest of the log (a tie vs AI and an unfinished game).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var first = await ReadEvent(reader);
        Assert.True(first.GetProperty("session").GetProperty("games").GetInt32() == 0, "unexpected first state: " + first);

        var lines = File.ReadAllLines(Fixture("synthetic_edge_cases.power.log"));
        var firstGameEnd = Array.FindIndex(lines, l => l.Contains("tag=STATE value=COMPLETE", StringComparison.Ordinal)) + 1;
        await File.AppendAllLinesAsync(Path.Combine(_session, "Power.log"), lines.Skip(firstGameEnd));

        // Bounded wait: on failure the message carries the last state (its warnings say what the host could not do).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var updated = first;
        var eventsSeen = 0;
        while (updated.GetProperty("session").GetProperty("games").GetInt32() == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, $"No completed game reported after {eventsSeen} event(s); last state: {updated}");
            updated = await ReadEvent(reader);
            eventsSeen++;
        }

        Assert.True(eventsSeen <= 5, $"Too many state events for one game ({eventsSeen}): the host publishes when nothing changed.");

        var session = updated.GetProperty("session");
        Assert.Equal(1, session.GetProperty("ties").GetInt32());
        Assert.Equal(0, session.GetProperty("wins").GetInt32());
        Assert.Equal("tie", updated.GetProperty("last_game").GetProperty("result").GetString());
        Assert.Equal("PRIEST", updated.GetProperty("last_game").GetProperty("player_class").GetString());
        Assert.Equal("WARRIOR", updated.GetProperty("last_game").GetProperty("opponent_class").GetString());

        // The unfinished third game is the current one.
        var state = await State();
        Assert.Equal("GT_RANKED", state.GetProperty("current_game").GetProperty("game_type").GetString());

        // Stored like 'games import' would store it; the pre-existing first game was skipped (not replayed into the session).
        var stored = Stored();
        Assert.Single(stored);
        Assert.Null(stored[0].DeckId);   // the only sample so far was taken on 2026-10-15 20:00, far from this game
        using (var store = DustStore.Open(Path.Combine(_home, "dustplanner.db"), readOnly: true))
        {
            Assert.NotNull(new RankRepository(store).Latest("8589934592-42", "standard"));
        }

        // HDT exports the active deck a few seconds into the game: the stored game gets linked to it on import.
        var inbox = Path.Combine(_home, "inbox", "8589934592-42");
        var export = ActiveDeck(stored[0].StartedAt.AddSeconds(5), "game_start");
        File.WriteAllText(Path.Combine(inbox, ExportSchema.ActiveDeckFileName), JsonSerializer.Serialize(export, CoreJson.Options));
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Stored()[0].DeckId is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The game was not linked to the active deck sample; state: " + await State());
            await Task.Delay(100);
        }

        var linked = Stored()[0];
        Assert.Equal(export.Meta.ExportedAt, (await State()).GetProperty("active_deck").GetProperty("updated_at").GetDateTimeOffset());
        using (var store = DustStore.Open(Path.Combine(_home, "dustplanner.db"), readOnly: true))
        {
            Assert.Equal("Synthetic Priest", store.Scalar("SELECT name FROM deck WHERE id = $d", ("$d", linked.DeckId)));
            Assert.Single(new ActiveDeckRepository(store).List("8589934592-42"), s => s.Reason == "game_start");
        }
    }

    private IReadOnlyList<StoredGame> Stored()
    {
        using var store = DustStore.Open(Path.Combine(_home, "dustplanner.db"), readOnly: true);
        return new GameRepository(store).List("8589934592-42", 10);
    }

    public void Dispose() => _http?.Dispose();

    private async Task<JsonElement> State()
    {
        var json = await _http!.GetStringAsync("/api/state");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<JsonElement> ReadEvent(StreamReader reader)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            var read = reader.ReadLineAsync();
            if (await Task.WhenAny(read, deadline) == deadline)
            {
                throw new TimeoutException("No SSE line within 20 s");
            }

            var line = await read ?? throw new EndOfStreamException("event stream closed");
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone();
            }
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
