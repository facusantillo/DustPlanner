# Connecting an AI client to DustPlanner (MCP)

`dustplanner-mcp` is a [Model Context Protocol](https://modelcontextprotocol.io/) server over **stdio**. It lets an
AI client (Claude Desktop, Claude Code, or any MCP-capable client) ask questions about your collection, get a
disenchant proposal for a deck, and read your match history — all **read-only**:

- It reads the JSON files the HDT exporter plugin writes, the local `dustplanner.db` (opened read-only) and the games
  parsed from `Power.log`. It never opens the game, HDT, or their memory.
- It never writes: no plan is stored (the CLI `plan` command does that), nothing is disenchanted, no file of yours is
  modified. The only thing it may write is the card-catalog cache under `%LocalAppData%\DustPlanner\catalog` when the
  catalog is missing.
- Every tool is annotated `readOnlyHint: true`, and the server instructions repeat the policy to the model.

## Build it once

```powershell
dotnet build src\DustPlanner.Mcp -c Release
```

The server is `src\DustPlanner.Mcp\bin\Release\net10.0\dustplanner-mcp.dll` (run with `dotnet`) or, after
`dotnet publish src\DustPlanner.Mcp -c Release -r win-x64 --self-contained false`, a `dustplanner-mcp.exe`.

Data folder: the same as the CLI (`%LocalAppData%\DustPlanner`, or `--home <folder>` / `DUSTPLANNER_HOME`). Run
`dustplanner sync` and `dustplanner games import` from the CLI first so there is something to read.

## Claude Desktop

Add to `%AppData%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "dustplanner": {
      "command": "dotnet",
      "args": ["C:\\Repos\\DustPlanner\\src\\DustPlanner.Mcp\\bin\\Release\\net10.0\\dustplanner-mcp.dll"]
    }
  }
}
```

Restart Claude Desktop; the five tools appear under the server name.

## Claude Code

```powershell
claude mcp add dustplanner -- dotnet C:\Repos\DustPlanner\src\DustPlanner.Mcp\bin\Release\net10.0\dustplanner-mcp.dll
```

## Tools

| Tool | Reads | Answers |
|---|---|---|
| `get_collection_summary(account?)` | exporter inbox, catalog | dust, gold, copies per premium kind, snapshot time, exported accounts |
| `get_missing_for_deck(deck, account?, asOf?)` | inbox, catalog, rules | missing cards with craft cost, shortfall, non-craftable / unknown cards, cards rotating next |
| `propose_disenchant_plan(deck, account?, need?, protectDeckCodes?, allowNoDecks?, asOf?)` | inbox, catalog, rules, `prefs.json`, `meta.txt`, `refunds.json`, database (saved decks) | the least painful disenchant set, alternatives, what was excluded and why, warnings (packs first) |
| `get_history(account?, limit?)` | database | snapshots, stored plans and their status, detected card changes, latest rank |
| `get_winrate(account?, since?, days?, gameType?, format?, recent?)` | database | win rate overall and per class, recent games |

`deck` accepts a deck code (`AAECA…`) or the deck block the game copies to the clipboard. Results are the same
snake_case JSON the CLI prints with `--json`. A bad input (invalid deck code, nothing exported yet, no database)
comes back as a tool error carrying the explanation, so the model can tell you what to run first.

Example questions once connected: *"What am I missing for this deck: AAECA…?"*, *"If I disenchant to build it, what
would you pick and why?"*, *"How is my Priest doing this week in ranked?"*.

## Verifying the server by hand

Any MCP inspector works; the test suite does the same thing programmatically
(`tests/DustPlanner.Mcp.Tests` starts the binary over stdio with the official client, lists the tools and calls each one
against a synthetic home folder, and checks that no database was created by the calls).
