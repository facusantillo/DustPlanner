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
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
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
