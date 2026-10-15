using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;
using DustPlanner.LogTracker;
using DustPlanner.Store;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DustPlanner.Mcp.Tests;

/// <summary>Drives the real server binary over stdio with the official client, against a synthetic home folder (no network).</summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private const string SyntheticDeck = "AAECAcwIA+sH8gePTgXqB+wH7QfwB/UHAAEB7wfrBwAA";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dustplanner-mcp-" + Guid.NewGuid().ToString("N"));
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        var catalogDir = Path.Combine(_home, "catalog", "synthetic");
        Directory.CreateDirectory(catalogDir);
        File.Copy(Fixture("cards.synthetic.json"), Path.Combine(catalogDir, "cards.collectible.enUS.json"));
        File.WriteAllText(Path.Combine(_home, "catalog", "manifest.json"),
            $$"""{ "build": "synthetic", "locale": "enUS", "checked_at": "{{DateTimeOffset.UtcNow:O}}" }""");
        var inbox = Path.Combine(_home, "inbox", "8589934592-42");
        Directory.CreateDirectory(inbox);
        File.Copy(Fixture("collection.synthetic.json"), Path.Combine(inbox, "collection.json"));

        var dll = Path.Combine(AppContext.BaseDirectory, "dustplanner-mcp.dll");
        Assert.True(File.Exists(dll), "MCP server not found next to the tests: " + dll);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "DustPlanner",
            Command = "dotnet",
            Arguments = [dll, "--home", _home],
        });
        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task Server_exposes_exactly_the_five_read_only_tools()
    {
        var tools = await _client!.ListToolsAsync();
        Assert.Equal(
            ["get_collection_summary", "get_history", "get_missing_for_deck", "get_winrate", "propose_disenchant_plan"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.Name + " must be annotated read-only"));
        Assert.Equal("DustPlanner", _client.ServerInfo.Name);
        Assert.Contains("read-only", _client.ServerInstructions ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collection_missing_and_plan_tools_answer_without_writing_anything()
    {
        var summary = await Call("get_collection_summary", new Dictionary<string, object?>());
        Assert.Equal(640, summary.GetProperty("collection").GetProperty("dust").GetInt32());
        Assert.Equal("8589934592-42", summary.GetProperty("accounts")[0].GetProperty("account_key").GetString());
        Assert.True(summary.GetProperty("collection").GetProperty("unopened_packs").ValueKind == JsonValueKind.Null);

        var missing = await Call("get_missing_for_deck", new Dictionary<string, object?> { ["deck"] = SyntheticDeck, ["asOf"] = "2026-10-15" });
        var report = missing.GetProperty("report");
        Assert.Equal(2240, report.GetProperty("total_craft_cost").GetInt32());
        Assert.Equal(1600, report.GetProperty("shortfall").GetInt32());
        Assert.Equal(4, report.GetProperty("missing").GetArrayLength());

        var refused = await CallRaw("propose_disenchant_plan", new Dictionary<string, object?> { ["deck"] = SyntheticDeck, ["asOf"] = "2026-10-15" });
        Assert.NotEqual(true, refused.IsError);
        var refusedPlan = Parse(refused).GetProperty("plan");
        Assert.False(refusedPlan.GetProperty("has_plan").GetBoolean()); // no saved decks known → blocked, as in the CLI
        Assert.NotEmpty(refusedPlan.GetProperty("blockers").EnumerateArray());

        var plan = (await Call("propose_disenchant_plan", new Dictionary<string, object?> { ["deck"] = SyntheticDeck, ["asOf"] = "2026-10-15", ["allowNoDecks"] = true })).GetProperty("plan");
        Assert.True(plan.GetProperty("has_plan").GetBoolean());
        Assert.Equal(1600, plan.GetProperty("main").GetProperty("total_dust").GetInt32());
        Assert.StartsWith("Open your", plan.GetProperty("warnings")[0].GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(plan.GetProperty("saved_plan_id").ValueKind == JsonValueKind.Null);

        // Read-only: no database was created by any of the calls above.
        Assert.False(File.Exists(Path.Combine(_home, "dustplanner.db")));
    }

    [Fact]
    public async Task Errors_are_tool_errors_with_the_message_not_protocol_errors()
    {
        var bad = await CallRaw("get_missing_for_deck", new Dictionary<string, object?> { ["deck"] = "not-a-deck-code" });
        Assert.True(bad.IsError);
        Assert.Contains("Invalid deck code", bad.Content.OfType<TextContentBlock>().Single().Text, StringComparison.Ordinal);

        var noDb = await CallRaw("get_winrate", new Dictionary<string, object?>());
        Assert.True(noDb.IsError);
        Assert.Contains("games import", noDb.Content.OfType<TextContentBlock>().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_and_winrate_read_the_database_the_cli_fills()
    {
        // Fill the database the way 'games import' would (the server opens it read-only afterwards).
        var dbPath = Path.Combine(_home, "dustplanner.db");
        using (var store = DustStore.Open(dbPath))
        {
            var logs = Path.Combine(_home, "Logs", "Hearthstone_2026_10_15_23_50_00");
            Directory.CreateDirectory(logs);
            File.Copy(Fixture("synthetic_edge_cases.power.log"), Path.Combine(logs, "Power.log"));
            new LogImporter(new GameRepository(store), null).ImportDirectory(Path.Combine(_home, "Logs"));
        }

        var winrate = await Call("get_winrate", new Dictionary<string, object?> { ["gameType"] = "GT_RANKED", ["since"] = "2026-10-01" });
        Assert.Equal("8589934592-42", winrate.GetProperty("account_key").GetString());
        Assert.Equal(2, winrate.GetProperty("overall").GetProperty("games").GetInt32());
        Assert.Equal(1, winrate.GetProperty("overall").GetProperty("losses").GetInt32());
        Assert.Equal(3, winrate.GetProperty("recent_games").GetArrayLength()); // recent games are not filtered

        var noSnapshot = await CallRaw("get_history", new Dictionary<string, object?>());
        Assert.True(noSnapshot.IsError); // games only: no collection snapshot synced yet
        Assert.Contains("sync", noSnapshot.Content.OfType<TextContentBlock>().Single().Text, StringComparison.Ordinal);

        // Now what 'dustplanner sync' does: a snapshot, a rank sample, an open plan — the tool must read them all
        // (the result is serialised after the connection is closed, so nothing may stay lazy).
        using (var store = DustStore.Open(dbPath))
        {
            var catalog = HearthstoneJsonParser.Parse(File.ReadAllText(Fixture("cards.synthetic.json")), "synthetic", "enUS");
            var rules = GameDataFiles.LoadRules(null);
            var collection = HdtExportImporter.ImportFile(Path.Combine(_home, "inbox", "8589934592-42", "collection.json"), rules.PremiumSlots);
            new HistoryService(store).Sync(collection, catalog, rules, Refunds.Empty, new DateOnly(2026, 10, 15), DateTimeOffset.UtcNow);
            new RankRepository(store).Import(new RankExport
            {
                Meta = new ExportMeta { Kind = "rank", ExportedAt = DateTimeOffset.UtcNow, Account = new AccountRef { Hi = 8589934592, Lo = 42 } },
                Formats = { ["standard"] = new RankInfo { LeagueId = 5, StarLevel = 6, Stars = 3 } },
            });
        }

        var history = await Call("get_history", new Dictionary<string, object?>());
        Assert.Equal("8589934592-42", history.GetProperty("account_key").GetString());
        Assert.Equal(1, history.GetProperty("snapshots").GetArrayLength());
        Assert.Equal("Diamond 5 (3 stars)", history.GetProperty("rank")[0].GetProperty("label").GetString());
        Assert.Equal(0, history.GetProperty("events").GetArrayLength());
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private async Task<CallToolResult> CallRaw(string tool, Dictionary<string, object?> arguments) =>
        await _client!.CallToolAsync(tool, arguments, cancellationToken: CancellationToken.None);

    private async Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments)
    {
        var result = await CallRaw(tool, arguments);
        Assert.NotEqual(true, result.IsError);
        return Parse(result);
    }

    private static JsonElement Parse(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
