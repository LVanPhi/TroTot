/**
 * Typed API client generated from /openapi/v1.json (`pnpm gen:api` writes schema.d.ts; never edit it by hand).
 * Browser: NEXT_PUBLIC_API_BASE_URL (empty in production = same origin via Caddy).
 * Token and refresh middleware arrive with sign-in (T1.x).
 */
import createClient from "openapi-fetch";

import type { paths } from "./schema";

export const api = createClient<paths>({
  baseUrl: process.env.NEXT_PUBLIC_API_BASE_URL ?? "",
  credentials: "include",
});
