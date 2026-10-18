import { describe, expect, it } from "vitest";
import { TokenCipher, constantTimeEqual, importRsaPublicKey, pkceChallenge, randomToken, sha256Hex, verifyKickSignature } from "../src/crypto";
import { makeSigner } from "./helpers";

describe("crypto", () => {
  it("encrypts tokens with a fresh IV every time and decrypts them back", async () => {
    const cipher = await TokenCipher.fromBase64Key("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");
    const a = await cipher.encrypt("refresh-token-value");
    const b = await cipher.encrypt("refresh-token-value");
    expect(a).not.toBe(b);
    expect(await cipher.decrypt(a)).toBe("refresh-token-value");
    expect(await cipher.decrypt(b)).toBe("refresh-token-value");
    await expect(cipher.decrypt(a.slice(0, -4) + "AAAA")).rejects.toBeTruthy();
    await expect(TokenCipher.fromBase64Key("c2hvcnQ=")).rejects.toThrow(/32 bytes/);
  });

  it("derives the PKCE S256 challenge and hex digests the way the service does", async () => {
    // RFC 7636 appendix B vector.
    expect(await pkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")).toBe("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    expect(await sha256Hex("abc")).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    expect(randomToken(32)).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(constantTimeEqual("abc", "abc")).toBe(true);
    expect(constantTimeEqual("abc", "abd")).toBe(false);
    expect(constantTimeEqual("abc", "abcd")).toBe(false);
  });

  it("verifies Kick's RSA PKCS#1 v1.5 signature over id.timestamp.body", async () => {
    const signer = await makeSigner();
    const key = await importRsaPublicKey(signer.pem);
    const body = JSON.stringify({ content: "!deck" });
    const data = new TextEncoder().encode(`01JMSG.2026-10-18T20:00:00Z.${body}`);
    const sig = btoa(String.fromCharCode(...new Uint8Array(await crypto.subtle.sign({ name: "RSASSA-PKCS1-v1_5" }, signer.privateKey, data))));

    expect(await verifyKickSignature(key, "01JMSG", "2026-10-18T20:00:00Z", body, sig)).toBe(true);
    expect(await verifyKickSignature(key, "01JMSG", "2026-10-18T20:00:01Z", body, sig)).toBe(false);
    expect(await verifyKickSignature(key, "01JMSG", "2026-10-18T20:00:00Z", body + " ", sig)).toBe(false);
    expect(await verifyKickSignature(key, "01JMSG", "2026-10-18T20:00:00Z", body, "not base64!!")).toBe(false);
    const other = await makeSigner();
    expect(await verifyKickSignature(await importRsaPublicKey(other.pem), "01JMSG", "2026-10-18T20:00:00Z", body, sig)).toBe(false);
  });
});
