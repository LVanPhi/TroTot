import { NextResponse, type NextRequest } from "next/server";

import { buildCsp, newNonce } from "@/lib/security/csp";

/**
 * Per-request CSP nonce (docs/07-security.md). Next.js reads the nonce from the request's CSP header and adds it to
 * its own scripts; pages are therefore rendered per request (no static shell, cacheComponents is off).
 */
export function proxy(request: NextRequest) {
  const nonce = newNonce();
  const csp = buildCsp(nonce, {
    apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL ?? "",
    mapStyleUrl: process.env.NEXT_PUBLIC_MAP_STYLE_URL ?? "https://tiles.openfreemap.org/styles/liberty",
    imageOrigins: process.env.NEXT_PUBLIC_IMAGE_ORIGINS ?? "",
    isDev: process.env.NODE_ENV === "development",
  });
  const headers = new Headers(request.headers);
  headers.set("x-nonce", nonce);
  headers.set("Content-Security-Policy", csp);
  const response = NextResponse.next({ request: { headers } });
  response.headers.set("Content-Security-Policy", csp);
  return response;
}

export const config = {
  matcher: [
    {
      // Pages only: static files, the service worker and the manifest need no nonce.
      source: "/((?!_next/static|_next/image|favicon.ico|icons/|sw.js|manifest.webmanifest).*)",
      missing: [
        { type: "header", key: "next-router-prefetch" },
        { type: "header", key: "purpose", value: "prefetch" },
      ],
    },
  ],
};
