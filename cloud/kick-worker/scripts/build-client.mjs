// Bundles the public deck page script (client/deck-page.ts) into src/generated/deck-page.js.txt, which the Worker
// imports as text and serves at /d/_/deck-page.js. The output is committed; CI rebuilds it and fails on a diff, so the
// deployed script is always the one the sources describe. Run: npm run build:client
import { build } from "esbuild";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const out = resolve(root, "src/generated/deck-page.js.txt");
mkdirSync(dirname(out), { recursive: true });

const result = await build({
  entryPoints: [resolve(root, "client/deck-page.ts")],
  bundle: true,
  format: "iife",
  target: ["es2022"],
  minify: true,
  legalComments: "none",
  write: false,
  charset: "utf8",
});
const code = result.outputFiles[0].text;
const header = "/* DustPlanner public deck page — built from client/deck-page.ts by scripts/build-client.mjs; do not edit. */\n";
const next = header + code;
let previous = "";
try {
  previous = readFileSync(out, "utf8");
} catch {
  // first build
}
if (previous !== next) {
  writeFileSync(out, next);
  console.log(`wrote ${out} (${next.length} bytes)`);
} else {
  console.log("deck-page.js.txt is up to date");
}
