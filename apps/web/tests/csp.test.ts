import { describe, expect, it } from "vitest";

import { buildCsp, newNonce } from "@/lib/security/csp";

const prod = { apiBaseUrl: "", mapStyleUrl: "https://tiles.openfreemap.org/styles/liberty", imageOrigins: "", isDev: false };

const directive = (csp: string, name: string) => csp.split("; ").find((d) => d.startsWith(`${name} `)) ?? "";

describe("CSP (docs/07-security.md)", () => {
  it("production: scripts only with the nonce, no eval, frames denied, upgrade to https", () => {
    const csp = buildCsp("abc", prod);
    expect(directive(csp, "script-src")).toBe("script-src 'self' 'nonce-abc' 'strict-dynamic'");
    expect(directive(csp, "frame-ancestors")).toBe("frame-ancestors 'none'");
    expect(directive(csp, "object-src")).toBe("object-src 'none'");
    expect(directive(csp, "base-uri")).toBe("base-uri 'self'");
    expect(directive(csp, "form-action")).toBe("form-action 'self'");
    expect(csp).toContain("upgrade-insecure-requests");
    expect(csp).not.toContain("unsafe-eval");
    expect(directive(csp, "img-src")).toContain("https://tiles.openfreemap.org");
    expect(directive(csp, "connect-src")).toBe("connect-src 'self' https://tiles.openfreemap.org");
  });

  it("dev: API on another origin for fetch and SignalR, Garage images, eval for React debugging", () => {
    const csp = buildCsp("n", { ...prod, apiBaseUrl: "http://localhost:8080", imageOrigins: "http://listing-images.web.garage.localhost:3902", isDev: true });
    expect(directive(csp, "connect-src")).toContain("http://localhost:8080 ws://localhost:8080");
    expect(directive(csp, "img-src")).toContain("http://listing-images.web.garage.localhost:3902");
    expect(directive(csp, "script-src")).toContain("'unsafe-eval'");
    expect(csp).not.toContain("upgrade-insecure-requests");
  });

  it("nonces are random", () => {
    expect(newNonce()).not.toBe(newNonce());
    expect(newNonce().length).toBeGreaterThanOrEqual(22);
  });
});
