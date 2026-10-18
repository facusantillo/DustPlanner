/** Hashing, random tokens, PKCE, token encryption and Kick's webhook signature. WebCrypto only. */

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export function toBase64Url(bytes: ArrayBuffer | Uint8Array): string {
  const arr = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let binary = "";
  for (const b of arr) {
    binary += String.fromCharCode(b);
  }
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

export function fromBase64(text: string): Uint8Array {
  const normalised = text.replaceAll("-", "+").replaceAll("_", "/");
  const padded = normalised + "=".repeat((4 - (normalised.length % 4)) % 4);
  const binary = atob(padded);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    out[i] = binary.charCodeAt(i);
  }
  return out;
}

export function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const b of bytes) {
    binary += String.fromCharCode(b);
  }
  return btoa(binary);
}

export function toHex(bytes: ArrayBuffer | Uint8Array): string {
  const arr = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  return Array.from(arr, (b) => b.toString(16).padStart(2, "0")).join("");
}

export function randomToken(bytes = 32): string {
  const buf = new Uint8Array(bytes);
  crypto.getRandomValues(buf);
  return toBase64Url(buf);
}

export async function sha256Hex(text: string): Promise<string> {
  return toHex(await crypto.subtle.digest("SHA-256", encoder.encode(text)));
}

/** PKCE S256: base64url(SHA-256(verifier)). */
export async function pkceChallenge(verifier: string): Promise<string> {
  return toBase64Url(await crypto.subtle.digest("SHA-256", encoder.encode(verifier)));
}

/** Constant-time comparison of two equal-length strings (hex digests); false when lengths differ. */
export function constantTimeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) {
    return false;
  }
  const ba = encoder.encode(a);
  const bb = encoder.encode(b);
  if (ba.byteLength !== bb.byteLength) {
    return false;
  }
  return crypto.subtle.timingSafeEqual(ba, bb);
}

/** AES-256-GCM with a random 12-byte IV per value; output is base64(iv ‖ ciphertext). */
export class TokenCipher {
  private constructor(private readonly key: CryptoKey) {}

  static async fromBase64Key(keyBase64: string): Promise<TokenCipher> {
    const raw = fromBase64(keyBase64);
    if (raw.byteLength !== 32) {
      throw new Error("TOKEN_ENCRYPTION_KEY must be 32 bytes (base64)");
    }
    const key = await crypto.subtle.importKey("raw", raw, { name: "AES-GCM" }, false, ["encrypt", "decrypt"]);
    return new TokenCipher(key);
  }

  async encrypt(plain: string): Promise<string> {
    const iv = new Uint8Array(12);
    crypto.getRandomValues(iv);
    const cipher = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv }, this.key, encoder.encode(plain)));
    const out = new Uint8Array(iv.byteLength + cipher.byteLength);
    out.set(iv, 0);
    out.set(cipher, iv.byteLength);
    return toBase64(out);
  }

  async decrypt(encoded: string): Promise<string> {
    const bytes = fromBase64(encoded);
    const iv = bytes.slice(0, 12);
    const cipher = bytes.slice(12);
    return decoder.decode(await crypto.subtle.decrypt({ name: "AES-GCM", iv }, this.key, cipher));
  }
}

/** Imports a PEM "PUBLIC KEY" (SPKI) block as an RSASSA-PKCS1-v1_5 / SHA-256 verification key. */
export async function importRsaPublicKey(pem: string): Promise<CryptoKey> {
  const body = pem
    .replace(/-----BEGIN PUBLIC KEY-----/g, "")
    .replace(/-----END PUBLIC KEY-----/g, "")
    .replace(/\s+/g, "");
  return crypto.subtle.importKey("spki", fromBase64(body), { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
}

/** Kick signs `"{message_id}.{timestamp}.{raw_body}"` with RSA PKCS#1 v1.5 + SHA-256 and sends it base64-encoded. */
export async function verifyKickSignature(key: CryptoKey, messageId: string, timestamp: string, rawBody: string, signatureBase64: string): Promise<boolean> {
  let signature: Uint8Array;
  try {
    signature = fromBase64(signatureBase64);
  } catch {
    return false;
  }
  const data = encoder.encode(`${messageId}.${timestamp}.${rawBody}`);
  try {
    return await crypto.subtle.verify({ name: "RSASSA-PKCS1-v1_5" }, key, signature, data);
  } catch {
    return false;
  }
}
