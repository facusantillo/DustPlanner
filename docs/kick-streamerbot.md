# Chat commands with Streamer.bot (Kick, Twitch or any chat it talks to)

DustPlanner already knows, live, which deck you are playing, your rank, today's record and your last game. From 1.1.0
the service turns that into **one-line chat answers** at

```
http://127.0.0.1:8765/api/chat/<command>?lang=es|en&user=<viewer>
```

and any chat bot that can fetch a URL can post them. This guide uses **Streamer.bot**, which supports Kick natively; the
native "Connect with Kick" button (no bot software at all) is a later stage of
[`PRD-kick.md`](PRD-kick.md).

Nothing here touches the game: the service reads the files HDT and the game write, and the bot posts text.

## What the endpoint answers

| Command | Aliases (default) | Example answer (Spanish) | When data is missing |
|---|---|---|---|
| `deck` | `mazo` | `Priest Control · AAECAa0G… · copialo y pegalo en Hearthstone` | `Todavía no hay mazo activo` |
| `rank` | `rango` | `Diamante 3 · Estándar` / `Leyenda #1.234 · Estándar` | `Todavía no hay rango exportado` |
| `wr` | `winrate` | `Hoy 7-3 (70 %) · sesión 4-1 · racha de 3 victorias` | `Todavía no hay partidas hoy` |
| `last` | `ultima` | `Última: victoria contra Mago en 9 turnos` | `Todavía no terminó ninguna partida` |
| `dust` | `polvo` | `1.240 de polvo` — **off by default**: your dust is your business | `Todavía no hay export de la colección` |

- **Status codes.** `200` with `text/plain; charset=utf-8` (one line, at most 500 characters, never starting with the
  command prefix). `204 No Content` while the command is in **cooldown** — post nothing. `404` for a word that is not a
  command or alias, or for a command you turned off.
- **`lang`** forces `es` or `en`; without it the saved preference applies, and without a preference the language of
  your Windows UI (Spanish → Spanish, anything else → English).
- **`user`** is optional. With it, the per-viewer cooldown applies on top of the global one (defaults: 10 s per command,
  30 s per viewer and command). Without it, only the global cooldown.
- Aliases work in the URL too: `/api/chat/mazo` is `/api/chat/deck`.
- The port is `8765` unless it was busy; the real address is in `%LocalAppData%\DustPlanner\service.json` and on the
  panel's Settings page under *Service*.

Everything in that table is configurable in the panel: **Settings → Chat commands** (prefix, language, cooldowns,
which commands are on, their aliases and a custom text per command with `{placeholders}`), stored in the `chat` block of
`prefs.json`.

## Streamer.bot: one command, step by step

Names below are from Streamer.bot 0.2.x; if your version words them differently, the shape is the same. Kick must be
connected first: *Platforms → Kick → Broadcaster Account → Login*, and the same account connected on the Streamer.bot
website (*Account Settings → Connections*) — see [Streamer.bot's Kick guide](https://docs.streamer.bot/guide/platforms/kick).

1. **Create the command.** *Commands → Add*: command `!deck`, enable the Kick (and/or Twitch) source, and set the
   Streamer.bot cooldowns to `0` — DustPlanner handles cooldowns itself and answers `204` when a command is cooling down.
2. **Create an action** (*Actions → Add*, name it `DustPlanner deck`) and attach the command to it as the trigger
   (*Triggers → Kick → Chat → Command Triggered*, pick `!deck`).
3. **Sub-action 1 — fetch the text.** *Core → Network → Fetch URL*:
   - **URL:** `http://127.0.0.1:8765/api/chat/deck?user=%user%`
   - **Variable Name:** `dpReply`
   - **Parse result as JSON:** off (the answer is plain text).
4. **Sub-action 2 — post it.** *Kick → Chat → Send Message to Channel* (or *Reply To Message* to answer the viewer
   directly) with the message `%dpReply%`.
   To stay quiet on a `204`, put an *If/Else* (*Core → Logic*) before it: if `%dpReply%` does not exist → *Break*.
   Streamer.bot's own guide for Fetch URL: <https://docs.streamer.bot/api/sub-actions/core/network/fetch-url>.
5. **Repeat** for `!rank`, `!wr` and `!last` (one command + one action each, only the URL changes), and `!dust` if you
   turned it on in Settings. Aliases such as `!mazo` can be added as extra commands pointing to the same action, or to
   the alias URL — both work.

Test from a browser first: open `http://127.0.0.1:8765/api/chat/deck?lang=es` and you should read the line the bot will
post. Then type `!deck` in your own chat.

## Tips

- Streamer.bot can also run the commands from Twitch or YouTube chat with the same actions: the endpoint does not care
  where the question came from.
- If you prefer the bot to answer in English for an English stream, add `&lang=en` to the URLs or change the language in
  Settings.
- Custom texts: in Settings you can replace any built-in text with your own one-liner using placeholders like
  `{deck_name}`, `{deckstring}`, `{rank}`, `{today_w}`, `{today_l}`, `{today_wr}`, `{session_w}`, `{session_l}`,
  `{streak_text}`, `{last_result}`, `{opponent_class}`, `{turns}`, `{dust}`, `{user}`. The full list is in the panel.
- When the native Kick connection arrives (stage K5), the panel will warn you to turn these Streamer.bot commands off so
  the chat does not get two answers.
