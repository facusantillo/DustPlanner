# OBS overlay (dustplanner-host)

`dustplanner-host` is a small local web server that follows the files the game and HDT write on their own —
`Power.log`, the exporter's `collection.json` and `rank.json` — and serves a transparent overlay for an
**OBS Browser Source**: current rank, session win rate, today's record, dust, and the last game.

It listens on **loopback only** (`http://127.0.0.1:8765`); nothing is reachable from the network. It never talks to
the game client: no hooks, no memory reads, no input. Completed games are stored in the local database exactly like
`dustplanner games import` does, so the CLI and the MCP server see them too.

## Run it

```powershell
dotnet run --project src\DustPlanner.Host -c Release
# or, after dotnet build: src\DustPlanner.Host\bin\Release\net10.0\dustplanner-host.exe
```

Options: `--port 8765`, `--home <folder>` (same data folder as the CLI), `--logs <Hearthstone\Logs>` when the folder
is not found through HDT's config, `--account <key|BattleTag>` when several accounts were exported,
`--poll-seconds 2`.

Open `http://127.0.0.1:8765/` once: the status page shows what was found (warnings when the logs folder or an export
is missing), the live state as JSON and a preview of the overlay.

## OBS

1. Sources → **+** → **Browser**.
2. URL `http://127.0.0.1:8765/overlay`, width **1920**, height **1080**, custom CSS empty. Leave *Shutdown source when
   not visible* off so the session keeps counting.
3. Position the bar by moving the source; the page itself is transparent apart from the bar in its top-left corner.

Query options (combine with `&`):

| Option | Effect |
|---|---|
| `?scale=1.25` | bigger / smaller bar (CSS scale, crisp at any value) |
| `?align=right` | bar anchored to the top-right corner instead |
| `?theme=light` | light pill for dark scenes |
| `?hide=today,dust` | hide cells: `rank`, `session`, `today`, `dust`, `last`; `streak` hides the "N in a row" note |

What the cells mean:

- **Rank** — latest ladder standing from the exporter (`rank.json`, refreshed by HDT when you visit the Play screen);
  Standard first, Wild when Standard has no rank this season.
- **Session** — games completed since the host started (win rate = wins ÷ decided games; draws listed separately).
- **Today** — games started since local midnight, from the database plus the session.
- **Dust** — balance from the latest collection export (updates when HDT refreshes the collection).
- **Last game** — result, classes and turns of the last completed game.

## How it updates

The host polls every 2 seconds: new lines of the newest `Power.log` session folder, and the modification time of the
two export files. The overlay keeps a Server-Sent Events connection (`/events`) and re-renders on every change;
`/api/state` returns the same JSON for anything else you want to build.

## Verification (VERIFICACION C5)

No OBS was available while building this; the overlay was checked with a headless Chromium at 1920×1080 (transparent
PNG, alpha 0 outside the bar) and the SSE stream with the test suite. Please confirm in OBS: transparent background,
readable at 1080p, the session updating a few seconds after a game ends, and the rank updating after the next export.
