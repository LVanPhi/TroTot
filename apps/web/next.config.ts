import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  // No cacheComponents / partial prerendering: a nonce-based CSP needs every page rendered per request
  // (docs/07-security.md; Next.js CSP guide: "PPR is incompatible with nonce-based CSP").
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
