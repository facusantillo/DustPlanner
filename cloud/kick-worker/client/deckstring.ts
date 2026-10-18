/**
 * Blizzard deckstring decoder for the public deck page (PRD §5.5). Runs in the viewer's browser; the relay never
 * decodes anything. Mirrors `DustPlanner.Core.Deckstrings.DeckstringCodec`:
 *
 *   0x00 | version=1 | format
 *   heroes:  n, dbfId×n
 *   cards×1: n, dbfId×n
 *   cards×2: n, dbfId×n
 *   cards×N: n, (dbfId, count)×n
 *   [0x01 | sideboards×1: n, (dbfId, owner)×n | sideboards×2: n, (dbfId, owner)×n | sideboards×N: n, (dbfId, count, owner)×n]
 *   [0x00 when there are no sideboards; older strings end right after cards×N]
 *
 * No card data here: ids only. Pure, so vitest checks it against the python-hearthstone vectors.
 */

export interface DeckCard {
  dbfId: number;
  count: number;
}

export interface SideboardCard extends DeckCard {
  ownerDbfId: number;
}

export interface DecodedDeck {
  format: number;
  heroes: number[];
  cards: DeckCard[];
  sideboards: SideboardCard[];
}

export class InvalidDeckstringError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "InvalidDeckstringError";
  }
}

export const DECKSTRING_VERSION = 1;

export function decodeDeckstring(deckstring: string): DecodedDeck {
  const bytes = fromBase64(deckstring.trim());
  const reader = new VarIntReader(bytes);
  if (reader.readByte() !== 0) {
    throw new InvalidDeckstringError("Deckstring does not start with the reserved 0x00 byte.");
  }
  const version = reader.readVarInt();
  if (version !== DECKSTRING_VERSION) {
    throw new InvalidDeckstringError(`Unsupported deckstring version ${version}.`);
  }
  const format = reader.readVarInt();

  const heroes: number[] = [];
  const heroCount = reader.readVarInt();
  for (let i = 0; i < heroCount; i++) {
    heroes.push(reader.readVarInt());
  }

  const cards: DeckCard[] = [];
  const x1 = reader.readVarInt();
  for (let i = 0; i < x1; i++) {
    cards.push({ dbfId: reader.readVarInt(), count: 1 });
  }
  const x2 = reader.readVarInt();
  for (let i = 0; i < x2; i++) {
    cards.push({ dbfId: reader.readVarInt(), count: 2 });
  }
  const xn = reader.readVarInt();
  for (let i = 0; i < xn; i++) {
    const dbfId = reader.readVarInt();
    const count = reader.readVarInt();
    cards.push({ dbfId, count });
  }

  const sideboards: SideboardCard[] = [];
  if (!reader.atEnd && reader.readByte() === 1) {
    const s1 = reader.readVarInt();
    for (let i = 0; i < s1; i++) {
      const dbfId = reader.readVarInt();
      sideboards.push({ dbfId, count: 1, ownerDbfId: reader.readVarInt() });
    }
    const s2 = reader.readVarInt();
    for (let i = 0; i < s2; i++) {
      const dbfId = reader.readVarInt();
      sideboards.push({ dbfId, count: 2, ownerDbfId: reader.readVarInt() });
    }
    const sn = reader.readVarInt();
    for (let i = 0; i < sn; i++) {
      const dbfId = reader.readVarInt();
      const count = reader.readVarInt();
      sideboards.push({ dbfId, count, ownerDbfId: reader.readVarInt() });
    }
  }

  heroes.sort((a, b) => a - b);
  cards.sort((a, b) => a.dbfId - b.dbfId);
  sideboards.sort((a, b) => (a.ownerDbfId !== b.ownerDbfId ? a.ownerDbfId - b.ownerDbfId : a.dbfId - b.dbfId));
  return { format, heroes, cards, sideboards };
}

class VarIntReader {
  private position = 0;

  constructor(private readonly bytes: Uint8Array) {}

  get atEnd(): boolean {
    return this.position >= this.bytes.length;
  }

  readByte(): number {
    if (this.atEnd) {
      throw new InvalidDeckstringError("Deckstring ends too early.");
    }
    return this.bytes[this.position++]!;
  }

  readVarInt(): number {
    let result = 0;
    let shift = 0;
    for (;;) {
      const b = this.readByte();
      result += (b & 0x7f) * 2 ** shift; // not `<<`: ids above 2^31 must not go negative
      if ((b & 0x80) === 0) {
        return result;
      }
      shift += 7;
      if (shift > 35) {
        throw new InvalidDeckstringError("Varint is too long.");
      }
    }
  }
}

function fromBase64(text: string): Uint8Array {
  if (text.length === 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(text)) {
    throw new InvalidDeckstringError("Deckstring is not valid base64.");
  }
  let binary: string;
  try {
    binary = atob(text);
  } catch {
    throw new InvalidDeckstringError("Deckstring is not valid base64.");
  }
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}
