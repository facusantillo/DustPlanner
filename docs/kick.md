# Kick: the native connection

> Work in progress: this page grows with the stages of [`PRD-kick.md`](PRD-kick.md). Today it covers connecting and
> disconnecting (K4). Answers in chat without Streamer.bot (K5), the public deck page (K6) and the stream events (K7)
> arrive in the next updates; until then, [`kick-streamerbot.md`](kick-streamerbot.md) is the way to get `!deck` answered.

## What it is

Settings → **Kick → Connect with Kick** links your channel to DustPlanner through a small relay at
`live.dustplanner.app`. Once connected, Kick sends the relay the chat lines that start with your prefix and name one of
your commands; the relay forwards those — and only those — to the DustPlanner service on your PC, which answers through
the relay as the bot account. Nothing from the game ever leaves your PC except the texts the bot posts.

## Connecting

1. Open the panel → Settings → Kick → **Connect with Kick**. A Kick tab opens (allow pop-ups for the panel if it does
   not) and asks you to authorise five permissions: read your user and channel, subscribe to events, write chat, read
   channel-point rewards. DustPlanner never asks for stream keys, moderation or channel edits.
2. Accept. The relay's page sends the browser back to the panel and the status card shows **Connected** as `@your_slug`.
   If the browser does not come back (another browser profile, a blocker), the page also shows a code: paste it under
   *Code shown by the Kick page* within two minutes.
3. Done. The service reconnects on its own every time it starts; you never repeat this unless you disconnect or Kick
   revokes the authorisation (the card then says **Connect again**).

While the beta is private, only channels on the relay's allowed list can connect; others see a "closed beta" page and
nothing about them is stored.

## Disconnecting

Settings → Kick → **Disconnect**. The relay deletes the webhook subscriptions, revokes both Kick tokens and forgets the
channel; the service deletes its device token (`%LocalAppData%\DustPlanner\kick.json`). You can also revoke DustPlanner
from Kick's own settings: the relay notices at the next renewal, asks you to connect again, and deletes the channel's
data 24 hours later.

## What is stored where

| On your PC | At the relay | Nowhere |
|---|---|---|
| a device token for the relay (DPAPI-protected, useless on another machine), your channel's id and slug | your Kick tokens, encrypted; the webhook subscription ids; a hash of each paired device; the last live status; the published deck when the public page is on | chat messages, your email, anything about the game |

Details, routes and the deploy procedure of the relay: [`cloud/kick-worker/README.md`](../cloud/kick-worker/README.md).

## Troubleshooting

- **The card stays on "Connecting…"** — the service cannot reach the relay (offline, firewall). Everything local keeps
  working; it retries with increasing delays up to a minute. The last error is shown under the status.
- **"Connect again"** — Kick no longer accepts the authorisation (revoked, expired, fewer permissions than needed). Press
  *Connect with Kick*; the previous pairing is discarded.
- **A link to `127.0.0.1:8765/kick/paired` answers "No pairing is in progress"** — expected: the service only accepts a
  code during the ten minutes after you pressed Connect. Press it again.
- Logs: the service prints lines starting with `Kick:`; the relay's `/health` shows whether it is configured.
