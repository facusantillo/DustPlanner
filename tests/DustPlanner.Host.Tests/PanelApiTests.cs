using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Json;
using DustPlanner.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DustPlanner.Host.Tests;

/// <summary>The JSON API behind the panel, against a synthetic home with an exported collection and decks.</summary>
public sealed class PanelApiTests : IAsyncLifetime, IDisposable
{
    private const string SyntheticDeck = "AAECAcwIA+sH8gePTgXqB+wH7QfwB/UHAAEB7wfrBwAA";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dustplanner-panel-" + Guid.NewGuid().ToString("N"));
    private string _inbox = string.Empty;
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var catalogDir = Path.Combine(_home, "catalog", "synthetic");
        Directory.CreateDirectory(catalogDir);
        File.Copy(Fixture("cards.synthetic.json"), Path.Combine(catalogDir, "cards.enUS.json"));
        File.WriteAllText(Path.Combine(_home, "catalog", "manifest.json"),
            $$"""{ "build": "synthetic", "locale": "enUS", "checked_at": "{{DateTimeOffset.UtcNow:O}}" }""");
        _inbox = Path.Combine(_home, "inbox", "8589934592-42");
        Directory.CreateDirectory(_inbox);
        File.Copy(Fixture("collection.synthetic.json"), Path.Combine(_inbox, "collection.json"));

        var decks = new DecksExport
        {
            Meta = new ExportMeta { Kind = "decks", ExportedAt = new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero), Trigger = "manual", Account = new AccountRef { Hi = 8589934592, Lo = 42, BattleTag = "Synthetic#0001", Region = "EU" } },
            Decks =
            {
                new DeckExport
                {
                    Origin = "hdt", ExternalId = "3f2a1", Name = "Dream Priest", CardClass = "Priest", IsWild = false, LastEdited = new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero),
                    Cards = { new DeckCardExport { DbfId = 1001, Count = 2 }, new DeckCardExport { DbfId = 1002, Count = 2 }, new DeckCardExport { DbfId = 1003, Count = 1 }, new DeckCardExport { DbfId = 1008, Count = 2 }, new DeckCardExport { DbfId = 1013, Count = 2 } },
                },
                new DeckExport { Origin = "hdt", ExternalId = "old", Name = "Old Wild", CardClass = "Priest", IsWild = true, Archived = true, Cards = { new DeckCardExport { DbfId = 1014, Count = 2 } } },
            },
        };
        File.WriteAllText(Path.Combine(_inbox, "decks.json"), JsonSerializer.Serialize(decks, CoreJson.Options));

        var options = new OverlayHostOptions { Home = _home, Port = 0, LogsDirectory = Path.Combine(_home, "no-logs"), PollInterval = TimeSpan.FromMilliseconds(200) };
        _app = HostApp.Create(options, quiet: true);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        TempDir.Delete(_home);
    }

    public void Dispose() => _http?.Dispose();

    [Fact]
    public async Task Overview_decks_need_and_plan_work_from_the_exported_decks()
    {
        var overview = await Get("/api/overview");
        Assert.Equal("Synthetic#0001", overview.GetProperty("account").GetProperty("battle_tag").GetString());
        Assert.Equal(640, overview.GetProperty("collection").GetProperty("dust").GetInt32());
        Assert.Equal(1, overview.GetProperty("deck_count").GetInt32()); // the archived deck is not counted
        Assert.Equal("synthetic", overview.GetProperty("catalog").GetProperty("build").GetString());

        var decks = await Get("/api/decks");
        var list = decks.GetProperty("decks").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("hdt:3f2a1", list[0].GetProperty("key").GetString());
        Assert.Equal("PRIEST", list[0].GetProperty("card_class").GetString());
        Assert.StartsWith("AAE", list[0].GetProperty("deckstring").GetString(), StringComparison.Ordinal);
        Assert.True(list[1].GetProperty("archived").GetBoolean());

        // A deck key works wherever a code does.
        var need = await Post("/api/need", new { deck = "hdt:3f2a1", as_of = "2026-10-15" });
        var report = need.GetProperty("report");
        Assert.Equal("Dream Priest", report.GetProperty("deck_name").GetString());
        Assert.Equal(1600, report.GetProperty("total_craft_cost").GetInt32());
        Assert.Equal(960, report.GetProperty("shortfall").GetInt32());

        var plan = await Post("/api/plan", new { deck = SyntheticDeck, as_of = "2026-10-15", allow_no_decks = true, save = true });
        var p = plan.GetProperty("plan");
        Assert.True(p.GetProperty("has_plan").GetBoolean(), plan.ToString());
        Assert.Equal(1006, p.GetProperty("main").GetProperty("picks")[0].GetProperty("dbf_id").GetInt32());
        var savedId = p.GetProperty("saved_plan_id").GetInt64();
        Assert.True(savedId > 0);

        var history = await Get("/api/history");
        var plans = history.GetProperty("plans").EnumerateArray().ToList();
        Assert.Single(plans);
        Assert.Equal(savedId, plans[0].GetProperty("plan").GetProperty("id").GetInt64());
        Assert.Equal("proposed", plans[0].GetProperty("plan").GetProperty("status").GetString());
        Assert.Equal("Titan Relic (synthetic)", plans[0].GetProperty("picks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_new_export_is_synced_automatically_and_matched_against_the_plan()
    {
        var plan = await Post("/api/plan", new { deck = SyntheticDeck, as_of = "2026-10-15", allow_no_decks = true, save = true });
        var planId = plan.GetProperty("plan").GetProperty("saved_plan_id").GetInt64();

        // The export present at start-up is imported as soon as the catalog is loaded: that is the baseline the change is measured against.
        await WaitForStateAsync(s => s.GetProperty("last_sync").ValueKind == JsonValueKind.Object, "initial sync");

        // The user disenchants golden Titan Relic in the game; the exporter writes a new collection.
        var file = Path.Combine(_inbox, "collection.json");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file))
            .Replace("\"1006\": [0, 1, 0, 0, 0, 0, 0, 0]", "\"1006\": [0, 0, 0, 0, 0, 0, 0, 0]", StringComparison.Ordinal)
            .Replace("\"dust\": 640", "\"dust\": 2240", StringComparison.Ordinal)
            .Replace("2026-10-14T20:10:00+02:00", "2026-10-15T09:00:00+02:00", StringComparison.Ordinal));

        var state = await WaitForStateAsync(s => s.GetProperty("last_sync").ValueKind == JsonValueKind.Object && s.GetProperty("last_sync").GetProperty("events").GetInt32() > 0, "automatic sync of the new export");

        Assert.Equal(1, state.GetProperty("last_sync").GetProperty("matched_events").GetInt32());
        Assert.Equal(1600, state.GetProperty("last_sync").GetProperty("dust_delta").GetInt32());

        var history = await Get("/api/history");
        var stored = history.GetProperty("plans").EnumerateArray().Single(x => x.GetProperty("plan").GetProperty("id").GetInt64() == planId);
        Assert.Equal("confirmed", stored.GetProperty("plan").GetProperty("status").GetString());
        var evt = history.GetProperty("events")[0];
        Assert.Equal("disenchanted", evt.GetProperty("event_kind").GetString());
        Assert.Equal("high", evt.GetProperty("confidence").GetString());

        // Asking again changes nothing.
        var again = await Post("/api/sync", new { });
        Assert.False(again.GetProperty("sync").GetProperty("new_snapshot").GetBoolean());
    }

    [Fact]
    public async Task Settings_round_trip_prefs_meta_and_card_overrides()
    {
        var prefs = await Get("/api/prefs");
        Assert.False(prefs.GetProperty("exists").GetBoolean());
        Assert.Equal("standard", prefs.GetProperty("prefs").GetProperty("format").GetString());

        var saved = await Put("/api/prefs", new { format = "wild", classes_played = new[] { "priest", "mage" }, plays_wild = true });
        Assert.Equal("wild", saved.GetProperty("prefs").GetProperty("format").GetString());
        Assert.Equal(new[] { "PRIEST", "MAGE" }, saved.GetProperty("prefs").GetProperty("classes_played").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.True(File.Exists(Path.Combine(_home, "prefs.json")));

        var bad = await _http!.PutAsJsonAsync("/api/prefs", new { format = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var meta = await Put("/api/meta", new { text = "### Meta Priest\n" + SyntheticDeck + "\n" });
        Assert.Equal(1, meta.GetProperty("deck_count").GetInt32());
        var badMeta = await _http!.PutAsJsonAsync("/api/meta", new { text = SyntheticDeck + "\nnot a code\n" });
        Assert.Equal(HttpStatusCode.BadRequest, badMeta.StatusCode);
        Assert.Contains("Line 2", await badMeta.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(SyntheticDeck, await File.ReadAllTextAsync(Path.Combine(_home, "meta.txt")), StringComparison.Ordinal); // the bad text was not written

        var search = await Get("/api/cards/search?q=titan");
        Assert.Equal(1006, search.GetProperty("cards")[0].GetProperty("dbf_id").GetInt32());

        var blocked = await Post("/api/cards/overrides", new { card = "titan relic", golden = true, uncraftable = true });
        Assert.Equal("Titan Relic (synthetic)", blocked.GetProperty("name").GetString());
        var overrides = await Get("/api/cards/overrides");
        var entry = Assert.Single(overrides.GetProperty("overrides").EnumerateArray());
        Assert.Equal("golden", entry.GetProperty("kind").GetString());
        Assert.Equal("craftable", entry.GetProperty("shipped_rule").GetString());

        // The blocked golden copy is no longer a candidate.
        var plan = await Post("/api/plan", new { deck = SyntheticDeck, as_of = "2026-10-15", allow_no_decks = true });
        Assert.False(plan.GetProperty("plan").GetProperty("has_plan").GetBoolean());

        var removed = await Delete("/api/cards/overrides/1006?golden=true");
        Assert.True(removed.GetProperty("removed").GetBoolean());
        Assert.Empty((await Get("/api/cards/overrides")).GetProperty("overrides").EnumerateArray());

        var error = await _http!.PostAsJsonAsync("/api/need", new { deck = "xyz" });
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("Invalid deck code", await error.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Polls /api/state until the condition holds (bounded); the failure message carries the last state.</summary>
    private async Task<JsonElement> WaitForStateAsync(Func<JsonElement, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (true)
        {
            var state = await Get("/api/state");
            if (condition(state))
            {
                return state;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}; last state: {state}");
            await Task.Delay(200);
        }
    }

    private async Task<JsonElement> Get(string path) => Parse(await _http!.GetStringAsync(path));

    private async Task<JsonElement> Post(string path, object body)
    {
        using var response = await _http!.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return Parse(text);
    }

    private async Task<JsonElement> Put(string path, object body)
    {
        using var response = await _http!.PutAsync(path, new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return Parse(text);
    }

    private async Task<JsonElement> Delete(string path)
    {
        using var response = await _http!.DeleteAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return Parse(text);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
