import { fileURLToPath } from "node:url";

import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: {
    alias: { "@": fileURLToPath(new URL(".", import.meta.url)) },
  },
  test: {
    include: ["tests/**/*.test.{ts,tsx}"],
    // Pure logic runs in node; component tests opt into jsdom with `// @vitest-environment jsdom`.
    environment: "node",
    restoreMocks: true,
  },
});
