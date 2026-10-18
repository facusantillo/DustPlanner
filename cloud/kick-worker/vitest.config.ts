import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.toml" },
      miniflare: {
        // Test-only values; the real ones are wrangler secrets. The key is 32 zero-ish bytes in base64.
        bindings: {
          KICK_CLIENT_SECRET: "test-client-secret",
          TOKEN_ENCRYPTION_KEY: "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
          KICK_CLIENT_ID: "test-client-id",
          BOT_USER_ID: "900",
          ALLOWED_SLUGS: "tsukuru_tv,other_allowed",
          PUBLIC_BASE_URL: "https://live.dustplanner.test",
        },
        kvNamespaces: ["CONFIG"],
      },
    }),
  ],
  test: {
    include: ["test/**/*.test.ts"],
    testTimeout: 20000,
  },
});
