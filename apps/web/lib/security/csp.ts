/**
 * Content Security Policy with a per-request nonce (docs/07-security.md, Đầu ra và trình duyệt). Pure so it can be
 * tested; proxy.ts builds a fresh nonce for every page request.
 */

export interface CspSources {
  /** NEXT_PUBLIC_API_BASE_URL; empty in production (same origin through Caddy). */
  apiBaseUrl: string;
  /** Map style/tiles host (OpenFreeMap by default). */
  mapStyleUrl: string;
  /** Extra image origins, e.g. Garage website endpoint in dev (comma separated). */
  imageOrigins: string;
  isDev: boolean;
}

const originOf = (url: string): string | null => {
  try {
    return new URL(url).origin;
  } catch {
    return null;
  }
};

/** http(s) origin → matching ws(s) origin for SignalR. */
const wsOf = (origin: string) => origin.replace(/^http/, "ws");

export function buildCsp(nonce: string, s: CspSources): string {
  const api = originOf(s.apiBaseUrl);
  const map = originOf(s.mapStyleUrl);
  const images = s.imageOrigins.split(",").map((o) => originOf(o.trim())).filter((o): o is string => o !== null);
  const connect = ["'self'", ...(api ? [api, wsOf(api)] : []), ...(map ? [map] : [])];
  const directives: Record<string, string[]> = {
    "default-src": ["'self'"],
    "script-src": ["'self'", `'nonce-${nonce}'`, "'strict-dynamic'", ...(s.isDev ? ["'unsafe-eval'"] : [])],
    // Styles may be inline: sonner and maplibre inject <style>/style="" at runtime without a nonce. Scripts stay
    // strict (nonce + strict-dynamic), which is what stops XSS; inline CSS alone cannot run code.
    "style-src": ["'self'", "'unsafe-inline'"],
    "img-src": ["'self'", "data:", "blob:", ...(map ? [map] : []), ...images],
    "font-src": ["'self'", "data:"],
    "connect-src": connect,
    // maplibre runs its tile workers from blob: URLs.
    "worker-src": ["'self'", "blob:"],
    "object-src": ["'none'"],
    "base-uri": ["'self'"],
    "form-action": ["'self'"],
    "frame-ancestors": ["'none'"],
  };
  const policy = Object.entries(directives).map(([k, v]) => `${k} ${v.join(" ")}`);
  if (!s.isDev) policy.push("upgrade-insecure-requests");
  return policy.join("; ");
}

/** 128 random bits, base64. */
export function newNonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes));
}
