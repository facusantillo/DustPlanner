/**
 * The WebSocket protocol between the relay and the local service (PRD §5.3). JSON text frames, every message carries
 * `v: 1`. Platform-neutral words on purpose: command, channel, stream.
 */
export const PROTOCOL_VERSION = 1;

export interface ChannelInfo {
  user_id: string;
  slug: string;
  username: string;
}

export interface LiveState {
  is_live: boolean;
  title: string | null;
  started_at: string | null;
  ended_at: string | null;
}

// ----- relay → service -----

export interface HelloMessage {
  v: 1;
  type: "hello";
  channel: ChannelInfo;
  needs_reconnect: boolean;
  missing_scopes: string[];
  live: LiveState;
  server_version: string;
}

export interface ChatCommandMessage {
  v: 1;
  type: "chat.command";
  message_id: string;
  command: string;
  args: string;
  sender: { user_id: string; username: string; badges: string[] };
  at: string;
}

export interface StreamStatusMessage extends LiveState {
  v: 1;
  type: "stream.status";
}

export interface RewardRedemptionMessage {
  v: 1;
  type: "reward.redemption";
  id: string;
  status: "pending" | "accepted" | "rejected";
  reward: { id: string; title: string; cost: number };
  redeemer: { username: string };
  user_input: string;
}

export type RelayErrorCode = "kick_rate_limited" | "kick_unavailable" | "send_dropped" | "needs_reconnect" | "bad_message";

export interface ErrorMessage {
  v: 1;
  type: "error";
  code: RelayErrorCode;
  detail: string;
}

export interface PongMessage {
  v: 1;
  type: "pong";
}

export type RelayToService = HelloMessage | ChatCommandMessage | StreamStatusMessage | RewardRedemptionMessage | ErrorMessage | PongMessage;

// ----- service → relay -----

export interface ConfigMessage {
  v: 1;
  type: "config";
  prefix: string;
  commands: { name: string; aliases: string[] }[];
  public_page: boolean;
  forward: { stream_status: boolean; redemptions: boolean; redemption_filter: string[] };
}

export interface ChatSendMessage {
  v: 1;
  type: "chat.send";
  text: string;
  reply_to_message_id?: string;
}

export interface DeckPublishMessage {
  v: 1;
  type: "deck.publish";
  name: string;
  deckstring: string;
  card_class: string | null;
  format: string | null;
}

export interface PingMessage {
  v: 1;
  type: "ping";
}

export type ServiceToRelay = ConfigMessage | ChatSendMessage | DeckPublishMessage | PingMessage;

/** The exact frames the hibernation auto-responder matches: the service must send this string byte for byte. */
export const PING_FRAME = JSON.stringify({ v: 1, type: "ping" });
export const PONG_FRAME = JSON.stringify({ v: 1, type: "pong" });

export function parseServiceMessage(raw: string | ArrayBuffer): ServiceToRelay | null {
  if (typeof raw !== "string" || raw.length > 16_384) {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== "object") {
    return null;
  }
  const m = parsed as Record<string, unknown>;
  if (m["v"] !== PROTOCOL_VERSION || typeof m["type"] !== "string") {
    return null;
  }
  switch (m["type"]) {
    case "ping":
      return { v: 1, type: "ping" };
    case "config": {
      const commands = Array.isArray(m["commands"]) ? m["commands"] : [];
      const forward = (m["forward"] && typeof m["forward"] === "object" ? m["forward"] : {}) as Record<string, unknown>;
      return {
        v: 1,
        type: "config",
        prefix: typeof m["prefix"] === "string" && m["prefix"].length > 0 && m["prefix"].length <= 3 ? m["prefix"] : "!",
        commands: commands
          .filter((c): c is Record<string, unknown> => Boolean(c) && typeof c === "object")
          .map((c) => ({
            name: String(c["name"] ?? "").toLowerCase(),
            aliases: Array.isArray(c["aliases"]) ? c["aliases"].filter((a): a is string => typeof a === "string").map((a) => a.toLowerCase()) : [],
          }))
          .filter((c) => /^[a-z0-9_]+$/.test(c.name))
          .slice(0, 32),
        public_page: m["public_page"] === true,
        forward: {
          stream_status: forward["stream_status"] === true,
          redemptions: forward["redemptions"] === true,
          redemption_filter: Array.isArray(forward["redemption_filter"]) ? forward["redemption_filter"].filter((x): x is string => typeof x === "string").slice(0, 64) : [],
        },
      };
    }
    case "chat.send":
      if (typeof m["text"] !== "string" || m["text"].trim().length === 0) {
        return null;
      }
      return { v: 1, type: "chat.send", text: m["text"], ...(typeof m["reply_to_message_id"] === "string" ? { reply_to_message_id: m["reply_to_message_id"] } : {}) };
    case "deck.publish":
      if (typeof m["name"] !== "string" || typeof m["deckstring"] !== "string") {
        return null;
      }
      return {
        v: 1,
        type: "deck.publish",
        name: m["name"].slice(0, 120),
        deckstring: m["deckstring"].slice(0, 400),
        card_class: typeof m["card_class"] === "string" ? m["card_class"] : null,
        format: typeof m["format"] === "string" ? m["format"] : null,
      };
    default:
      return null;
  }
}
