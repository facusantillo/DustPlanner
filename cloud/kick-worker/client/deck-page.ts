/**
 * The public deck page, browser side (PRD §5.5). The relay sends name, deckstring, class, format and time; this script
 * decodes the deckstring here and asks HearthstoneJSON for names and costs, exactly like the rest of DustPlanner
 * (PROPUESTA §10.1.3: no Blizzard card data on the relay). No cookies, no analytics, no third party besides
 * HearthstoneJSON.
 */
import { decodeDeckstring, type DecodedDeck } from "./deckstring";

interface PageData {
  lang: "es" | "en";
  channel: { slug: string; username: string };
  deck: { name: string; deckstring: string; card_class: string | null; format: string | null; updated_at: string };
}

interface CardInfo {
  dbfId: number;
  name: string;
  cost: number;
  rarity?: string;
  type?: string;
}

const TEXT = {
  es: {
    loading: "Cargando cartas…",
    cards: "cartas",
    copy: "Copiar código",
    copied: "Código copiado",
    copyFailed: "No se pudo copiar: seleccioná el código de abajo",
    badCode: "El código del mazo no se pudo leer.",
    noCards: "No se pudieron cargar los datos de cartas; el código igual sirve.",
    updated: "Actualizado",
    justNow: "recién",
    minutesAgo: (n: number) => `hace ${n} min`,
    hoursAgo: (n: number) => `hace ${n} h`,
    sideboard: "Dentro de",
    unknown: "Carta",
  },
  en: {
    loading: "Loading cards…",
    cards: "cards",
    copy: "Copy code",
    copied: "Code copied",
    copyFailed: "Could not copy: select the code below",
    badCode: "The deck code could not be read.",
    noCards: "Card data could not be loaded; the code still works.",
    updated: "Updated",
    justNow: "just now",
    minutesAgo: (n: number) => `${n} min ago`,
    hoursAgo: (n: number) => `${n} h ago`,
    sideboard: "Inside",
    unknown: "Card",
  },
} as const;

const HEARTHSTONEJSON = "https://api.hearthstonejson.com/v1/latest";

function main(): void {
  const raw = document.getElementById("deck-data")?.textContent;
  if (!raw) {
    return;
  }
  const data = JSON.parse(raw) as PageData;
  const t = TEXT[data.lang] ?? TEXT.en;
  const list = document.getElementById("cards")!;
  const status = document.getElementById("status")!;
  const copyButton = document.getElementById("copy") as HTMLButtonElement | null;
  const updated = document.getElementById("updated");

  if (updated) {
    updated.textContent = `${t.updated} ${relative(data.deck.updated_at, t)}`;
  }

  copyButton?.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(data.deck.deckstring);
      flash(copyButton, t.copied);
    } catch {
      status.textContent = t.copyFailed;
      selectCode();
    }
  });

  let decoded: DecodedDeck;
  try {
    decoded = decodeDeckstring(data.deck.deckstring);
  } catch {
    status.textContent = t.badCode;
    return;
  }

  const total = decoded.cards.reduce((n, c) => n + c.count, 0);
  status.textContent = t.loading;
  const locale = data.lang === "es" ? "esES" : "enUS";
  fetch(`${HEARTHSTONEJSON}/${locale}/cards.collectible.json`, { headers: { accept: "application/json" } })
    .then((r) => (r.ok ? (r.json() as Promise<CardInfo[]>) : Promise.reject(new Error(String(r.status)))))
    .then((cards) => {
      const byId = new Map<number, CardInfo>();
      for (const card of cards) {
        byId.set(card.dbfId, card);
      }
      render(list, decoded, byId, t);
      status.textContent = `${total} ${t.cards}`;
    })
    .catch(() => {
      render(list, decoded, new Map(), t);
      status.textContent = `${total} ${t.cards} · ${t.noCards}`;
    });
}

function render(list: HTMLElement, deck: DecodedDeck, byId: Map<number, CardInfo>, t: (typeof TEXT)[keyof typeof TEXT]): void {
  const rows = deck.cards
    .map((c) => ({ ...c, info: byId.get(c.dbfId) }))
    .sort((a, b) => (a.info?.cost ?? 99) - (b.info?.cost ?? 99) || (a.info?.name ?? "").localeCompare(b.info?.name ?? "") || a.dbfId - b.dbfId);
  const sideboards = new Map<number, typeof rows>();
  for (const s of deck.sideboards) {
    const entry = { dbfId: s.dbfId, count: s.count, info: byId.get(s.dbfId) };
    const owned = sideboards.get(s.ownerDbfId) ?? [];
    owned.push(entry);
    sideboards.set(s.ownerDbfId, owned);
  }

  list.replaceChildren();
  for (const row of rows) {
    list.appendChild(cardRow(row, t));
    const inside = sideboards.get(row.dbfId);
    if (inside) {
      const sub = document.createElement("ul");
      sub.className = "sideboard";
      sub.setAttribute("aria-label", `${t.sideboard} ${row.info?.name ?? row.dbfId}`);
      for (const s of inside.sort((a, b) => (a.info?.cost ?? 99) - (b.info?.cost ?? 99) || a.dbfId - b.dbfId)) {
        sub.appendChild(cardRow(s, t));
      }
      list.appendChild(sub);
    }
  }
}

function cardRow(row: { dbfId: number; count: number; info?: CardInfo }, t: (typeof TEXT)[keyof typeof TEXT]): HTMLLIElement {
  const li = document.createElement("li");
  li.className = "card" + (row.info?.rarity === "LEGENDARY" ? " legendary" : "");
  const cost = document.createElement("span");
  cost.className = "cost";
  cost.textContent = row.info ? String(row.info.cost) : "?";
  const name = document.createElement("span");
  name.className = "name";
  name.textContent = row.info?.name ?? `${t.unknown} #${row.dbfId}`;
  const count = document.createElement("span");
  count.className = "count";
  count.textContent = row.count > 1 ? `×${row.count}` : "";
  li.append(cost, name, count);
  return li;
}

function relative(iso: string, t: (typeof TEXT)[keyof typeof TEXT]): string {
  const at = Date.parse(iso);
  if (!Number.isFinite(at)) {
    return "";
  }
  const minutes = Math.max(0, Math.round((Date.now() - at) / 60_000));
  if (minutes < 1) {
    return t.justNow;
  }
  if (minutes < 60) {
    return t.minutesAgo(minutes);
  }
  if (minutes < 48 * 60) {
    return t.hoursAgo(Math.round(minutes / 60));
  }
  return new Date(at).toLocaleDateString();
}

function flash(button: HTMLButtonElement, text: string): void {
  const original = button.textContent;
  button.textContent = text;
  button.disabled = true;
  setTimeout(() => {
    button.textContent = original;
    button.disabled = false;
  }, 1500);
}

function selectCode(): void {
  const code = document.getElementById("code");
  if (!code) {
    return;
  }
  const range = document.createRange();
  range.selectNodeContents(code);
  const selection = window.getSelection();
  selection?.removeAllRanges();
  selection?.addRange(range);
}

main();
