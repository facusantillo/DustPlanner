# Security and game-client policy

DustPlanner is a read-only companion for Hearthstone. This document states, in
one place, what the project will and will not do with the game client, so that
anyone auditing the code (or deciding whether to run it) knows where the line is.

## What DustPlanner never does

1. **No input automation.** DustPlanner never sends clicks, key presses, or any
   other input to the Hearthstone client. It never scripts the collection
   manager, never disenchants, never crafts. Every change to your collection is
   made by you, by hand, in the game.
2. **No memory reading of its own.** DustPlanner does not read, scan, or hook
   the Hearthstone process. It ships no native code, no `DllImport` of
   process-inspection APIs, and no copy of HearthMirror.
3. **No injection.** Nothing is loaded into the game process.
4. **No network calls to Blizzard services.** The only remote resource the
   tool fetches is the public card catalog from HearthstoneJSON, and only when
   you run a command that needs it.
5. **No scraping** of third-party sites for meta data. Meta signals come from
   deck codes you paste into a local file.

## What it does instead

- The HDT exporter is a standard [Hearthstone Deck Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker)
  plugin. It uses only HDT's public plugin API (`IPlugin`, `GameEvents`,
  `CollectionHelpers`, `HearthMirror.Reflection.Client`) to read the data HDT
  already holds in memory, and writes it to JSON files on your disk. It adds no
  new access path to the game beyond what HDT itself performs.
- The log tracker reads `Power.log`, a plain-text file the game writes when
  logging is enabled in `log.config` — the same mechanism every deck tracker uses.
  It opens the file read-only and shared, never the process. The only file it can
  write outside its own data folder is Blizzard's `log.config`, and only when you
  run `dustplanner games log-config --apply`: it adds or completes the `[Power]`
  section (the settings HDT itself writes) and leaves every other line untouched.
- Everything else (planner, store, MCP server, overlay host) works on local
  files and a local SQLite database.

## Reporting a concern

If you believe any part of the code base violates the policy above, open an
issue titled `policy:` with the file and line. Pull requests that add input
automation, memory reading, or scraping will not be merged.

## Scope note

Hearthstone Deck Tracker itself reads the game client's memory (through
HearthMirror) and has been tolerated by Blizzard for years, including in
official tournaments under the Tournament Player Handbook. DustPlanner relies
on that existing, well-known mechanism and does not extend it. Using HDT is
your decision; review Blizzard's current End User License Agreement before you
do.
