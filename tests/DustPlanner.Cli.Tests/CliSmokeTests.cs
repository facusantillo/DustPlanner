using System.Diagnostics;
using System.Text.Json;

namespace DustPlanner.Cli.Tests;

/// <summary>Runs the real CLI binary against a synthetic home folder (cached catalog + exporter inbox), no network.</summary>
public sealed class CliSmokeTests : IDisposable
{
    private const string SyntheticDeck = "AAECAcwIA+sH8gePTgXqB+wH7QfwB/UHAAEB7wfrBwAA";
    private readonly string _home;

    public CliSmokeTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "dustplanner-cli-" + Guid.NewGuid().ToString("N"));
        var catalogDir = Path.Combine(_home, "catalog", "synthetic");
        Directory.CreateDirectory(catalogDir);
        File.Copy(Fixture("cards.synthetic.json"), Path.Combine(catalogDir, "cards.collectible.enUS.json"));
        File.WriteAllText(Path.Combine(_home, "catalog", "manifest.json"),
            $$"""{ "build": "synthetic", "locale": "enUS", "checked_at": "{{DateTimeOffset.UtcNow:O}}" }""");
        var inbox = Path.Combine(_home, "inbox", "8589934592-42");
        Directory.CreateDirectory(inbox);
        File.Copy(Fixture("collection.synthetic.json"), Path.Combine(inbox, "collection.json"));
    }

    public void Dispose()
    {
        TempDir.Delete(_home);
    }

    [Fact]
    public async Task Need_prints_missing_cards_totals_and_rotation()
    {
        var (code, stdout, stderr) = await Run("need", SyntheticDeck, "--as-of", "2026-10-15");

        Assert.True(code == 0, stderr + stdout);
        Assert.Contains("MISSING", stdout, StringComparison.Ordinal);
        Assert.Contains("Dream Keeper (synthetic)", stdout, StringComparison.Ordinal);
        Assert.Contains("Workshop Gadget (synthetic) (sideboard)", stdout, StringComparison.Ordinal);
        Assert.Contains("Total to craft", stdout, StringComparison.Ordinal);
        Assert.Contains("2,240", stdout, StringComparison.Ordinal);
        Assert.Contains("Still needed", stdout, StringComparison.Ordinal);
        Assert.Contains("1,600", stdout, StringComparison.Ordinal);
        Assert.Contains("ROTATES AT NEXT ROTATION", stdout, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN CRAFTABILITY", stdout, StringComparison.Ordinal);
        Assert.Contains("dbfId 9999", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Need_json_is_snake_case_and_consistent()
    {
        var (code, stdout, stderr) = await Run("--json", "need", SyntheticDeck, "--as-of", "2026-10-15", "--dust", "3000");

        Assert.True(code == 0, stderr + stdout);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("PRIEST", root.GetProperty("deck_class").GetString());
        Assert.Equal(2240, root.GetProperty("total_craft_cost").GetInt32());
        Assert.Equal(3000, root.GetProperty("dust_available").GetInt32());
        Assert.Equal(0, root.GetProperty("shortfall").GetInt32());
        Assert.Equal(4, root.GetProperty("missing").GetArrayLength());
        Assert.Equal(2000, root.GetProperty("rotation").GetProperty("dust_at_risk").GetInt32());
    }

    [Fact]
    public async Task Plan_json_proposes_the_least_painful_cover_and_warns_about_packs()
    {
        var (code, stdout, stderr) = await Run("--json", "plan", SyntheticDeck, "--as-of", "2026-10-15", "--allow-no-decks");

        Assert.True(code == 0, stderr + stdout);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("has_plan").GetBoolean());
        Assert.Equal(1600, root.GetProperty("need").GetInt32());
        var main = root.GetProperty("main");
        Assert.Equal(1600, main.GetProperty("total_dust").GetInt32());
        Assert.Equal(3, main.GetProperty("total_pain").GetInt32());
        var pick = main.GetProperty("picks")[0];
        Assert.Equal("golden", pick.GetProperty("kind").GetString());
        Assert.Equal(1006, pick.GetProperty("dbf_id").GetInt32());
        Assert.StartsWith("Open your pending packs", root.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(1600, root.GetProperty("missing").GetProperty("shortfall").GetInt32());
    }

    [Fact]
    public async Task Plan_without_saved_decks_is_blocked_with_exit_code_1()
    {
        var (code, stdout, _) = await Run("plan", SyntheticDeck, "--as-of", "2026-10-15");

        Assert.Equal(1, code);
        Assert.Contains("NO DISENCHANT PLAN", stdout, StringComparison.Ordinal);
        Assert.Contains("No saved decks are known", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_plan_disenchant_sync_matches_the_plan_and_history_shows_it()
    {
        var (c1, out1, err1) = await Run("sync");
        Assert.True(c1 == 0, err1 + out1);
        Assert.Contains("imported", out1, StringComparison.Ordinal);

        var (c2, out2, err2) = await Run("plan", SyntheticDeck, "--as-of", "2026-10-15", "--allow-no-decks");
        Assert.True(c2 == 0, err2 + out2);
        Assert.Contains("Plan #1 stored", out2, StringComparison.Ordinal);

        // Simulate the user disenchanting the golden Titan Relic and exporting again.
        var file = Path.Combine(_home, "inbox", "8589934592-42", "collection.json");
        File.WriteAllText(file, File.ReadAllText(file)
            .Replace("\"1006\": [0, 1, 0, 0, 0, 0, 0, 0]", "\"1006\": [0, 0, 0, 0, 0, 0, 0, 0]", StringComparison.Ordinal)
            .Replace("\"dust\": 640", "\"dust\": 2240", StringComparison.Ordinal)
            .Replace("2026-10-14T20:10:00+02:00", "2026-10-15T09:00:00+02:00", StringComparison.Ordinal));

        var (c3, out3, err3) = await Run("--json", "sync");
        Assert.True(c3 == 0, err3 + out3);
        using var doc = JsonDocument.Parse(out3);
        var summary = doc.RootElement[0];
        Assert.Equal(1600, summary.GetProperty("dust_delta").GetInt32());
        var evt = summary.GetProperty("events")[0];
        Assert.Equal("disenchanted", evt.GetProperty("event_kind").GetString());
        Assert.Equal(1, evt.GetProperty("plan_id").GetInt64());
        Assert.Equal("high", evt.GetProperty("confidence").GetString());

        var (c4, out4, err4) = await Run("history");
        Assert.True(c4 == 0, err4 + out4);
        Assert.Contains("confirmed", out4, StringComparison.Ordinal);
        Assert.Contains("Titan Relic (synthetic)", out4, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collection_list_and_show_read_the_inbox()
    {
        var (code, stdout, _) = await Run("collection", "list");
        Assert.Equal(0, code);
        Assert.Contains("Synthetic#0001", stdout, StringComparison.Ordinal);

        var (code2, stdout2, stderr2) = await Run("collection", "show");
        Assert.True(code2 == 0, stderr2 + stdout2);
        Assert.Contains("Dust:           640", stdout2, StringComparison.Ordinal);
        Assert.Contains("unknown kind 1", stdout2, StringComparison.Ordinal);
        Assert.Contains("Unopened packs: unknown", stdout2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_deck_code_fails_with_a_clear_message()
    {
        var (code, _, stderr) = await Run("need", "not-a-deck-code");
        Assert.Equal(2, code);
        Assert.Contains("Invalid deck code", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_info_reports_the_year_and_unconfirmed_slots()
    {
        var (code, stdout, stderr) = await Run("catalog", "info");
        Assert.True(code == 0, stderr + stdout);
        Assert.Contains("build synthetic", stdout, StringComparison.Ordinal);
        Assert.Contains("unconfirmed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Games_import_list_stats_and_log_config_work_end_to_end()
    {
        var logs = Path.Combine(_home, "Logs");
        var session = Path.Combine(logs, "Hearthstone_2026_10_15_23_50_00");
        Directory.CreateDirectory(session);
        File.Copy(Fixture("synthetic_edge_cases.power.log"), Path.Combine(session, "Power.log"));

        var (c1, out1, err1) = await Run("games", "import", "--logs", logs);
        Assert.True(c1 == 0, err1 + out1);
        Assert.Contains("1 Power.log file(s), 3 game(s) parsed, 3 new, 1 without a result", out1, StringComparison.Ordinal);

        var (c2, out2, err2) = await Run("--json", "games", "import", "--logs", logs);
        Assert.True(c2 == 0, err2 + out2);
        using (var doc = JsonDocument.Parse(out2))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("games_inserted").GetInt32());
        }

        var (c3, out3, err3) = await Run("games", "list");
        Assert.True(c3 == 0, err3 + out3);
        Assert.Contains("Account 8589934592-42 · 3 game(s)", out3, StringComparison.Ordinal);
        Assert.Contains("Rival#2222", out3, StringComparison.Ordinal);
        Assert.Contains("The Innkeeper", out3, StringComparison.Ordinal);

        var (c4, out4, err4) = await Run("--json", "games", "stats", "--since", "2026-10-01", "--type", "GT_RANKED");
        Assert.True(c4 == 0, err4 + out4);
        using (var doc = JsonDocument.Parse(out4))
        {
            var overall = doc.RootElement.GetProperty("overall");
            Assert.Equal(2, overall.GetProperty("games").GetInt32());
            Assert.Equal(1, overall.GetProperty("losses").GetInt32());
            Assert.Equal(1, overall.GetProperty("unknown").GetInt32());
            Assert.Equal(0.0, overall.GetProperty("win_rate").GetDouble());
        }

        var (c5, out5, _) = await Run("games", "stats");
        Assert.True(c5 == 0, out5);
        Assert.Contains("3 game(s): 0 won, 1 lost, 1 tied, 1 unknown", out5, StringComparison.Ordinal);
        Assert.Contains("Priest", out5, StringComparison.Ordinal);

        var config = Path.Combine(_home, "log.config");
        var (c6, out6, _) = await Run("games", "log-config", "--path", config);
        Assert.Equal(1, c6);
        Assert.Contains("missing", out6, StringComparison.Ordinal);
        var (c7, out7, _) = await Run("games", "log-config", "--path", config, "--apply");
        Assert.Equal(0, c7);
        Assert.Contains("(written)", out7, StringComparison.Ordinal);
        Assert.Contains("[Power]", await File.ReadAllTextAsync(config), StringComparison.Ordinal);
        var (c8, _, _) = await Run("games", "log-config", "--path", config);
        Assert.Equal(0, c8);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "dustplanner.dll");
        Assert.True(File.Exists(dll), "CLI not found next to the tests: " + dll);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--home");
        psi.ArgumentList.Add(_home);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
