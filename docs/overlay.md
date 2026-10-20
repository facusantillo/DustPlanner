# OBS overlay (dustplanner-host)

`dustplanner-host` is a small local web server that follows the files the game and HDT write on their own —
`Power.log`, the exporter's `collection.json`, `rank.json` and `active-deck.json` — and serves a transparent overlay for an
**OBS Browser Source**: current rank, session win rate, today's record, dust, and the last game.

It listens on **loopback only** (`http://127.0.0.1:8765`); nothing is reachable from the network. It never talks to
the game client: no hooks, no memory reads, no input. Completed games are stored in the local database exactly like
`dustplanner games import` does, so the CLI and the MCP server see them too.

## Run it

With the HDT plugin installed, nothing: the plugin starts the service when HDT loads it and stops it with HDT (the
address is in `%LocalAppData%\DustPlanner\service.json`, normally `http://127.0.0.1:8765`). From source:

```powershell
dotnet run --project src\DustPlanner.Host -c Release -- --open
# or, after dotnet build: src\DustPlanner.Host\bin\Release\net10.0\dustplanner-host.exe
```

Options: `--port 8765` (the next free port of ten is used when it is busy), `--home <folder>` (same data folder as the
CLI), `--logs <Hearthstone\Logs>` when the folder is not found through HDT's config, `--account <key|BattleTag>` to pin
one account, `--poll-seconds 2`, `--open` (open the panel in the browser).

Without `--account` the host follows the account being played: the one whose exporter files (`collection.json`,
`decks.json`, `rank.json`, `active-deck.json`) or last finished game are the newest, checked on every poll. Switching
region or account in Hearthstone moves the overlay, the chat answers and the public deck page to that account within a
few seconds; dust, rank and deck of the previous one are dropped until its own exports are newer again.

`http://127.0.0.1:8765/` is the panel ([`panel.md`](panel.md)); `http://127.0.0.1:8765/status` is the host status page:
what was found (warnings when the logs folder or an export is missing), the live state as JSON and a preview of the
overlay.

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
| `?hide=today,dust` | hide cells: `rank`, `session`, `today`, `dust`, `last`, `deck`; `streak` hides the "N in a row" note |

What the cells mean:

- **Rank** — latest ladder standing from the exporter (`rank.json`, refreshed by HDT when you visit the Play screen);
  Standard first, Wild when Standard has no rank this season.
- **Session** — games completed since the host started (win rate = wins ÷ decided games; draws listed separately).
- **Today** — games started since local midnight, from the database plus the session.
- **Dust** — balance from the latest collection export (updates when HDT refreshes the collection).
- **Last game** — result, classes and turns of the last completed game.
- **Deck** — the deck selected in HDT (`active-deck.json`, written when a game starts, when you pick another deck in
  HDT, and on the menu transitions that already export). Hidden until the exporter has written one, and for Arena.
  The same sample links each stored game to its deck (`game.deck_id`), which is what per-deck win rates rest on.

## Kick redemptions (K7)

With the Kick connection on and *Show channel-point redemptions in the overlay* ticked (Settings → Kick), a card
appears under the bar for 6 seconds whenever a viewer redeems a reward: the reward's title, the viewer and what they
typed. Several in a row queue up. Each redemption shows once, even if Kick reports it twice (pending, then accepted);
the optional filter keeps only the reward ids you list. The card follows `?scale=` and `?align=right`.

## How it updates

The host polls every 2 seconds: new lines of the newest `Power.log` session folder, and the modification time of the
three export files. The overlay keeps a Server-Sent Events connection (`/events`) and re-renders on every change;
`/api/state` returns the same JSON for anything else you want to build.

## Verification (VERIFICACION C5)

No OBS was available while building this; the overlay was checked with a headless Chromium at 1920×1080 (transparent
PNG, alpha 0 outside the bar) and the SSE stream with the test suite. Please confirm in OBS: transparent background,
readable at 1080p, the session updating a few seconds after a game ends, and the rank updating after the next export.
