/**
 * Typed API client generated from /openapi/v1.json (`pnpm gen:api` writes schema.d.ts; never edit it by hand).
 * Browser: NEXT_PUBLIC_API_BASE_URL (empty in production = same origin via Caddy).
 * Requests go through authFetch: bearer token from the in-memory session, one refresh-and-retry on 401.
 */
import createClient from "openapi-fetch";

import { CSRF_HEADER, CSRF_VALUE, createAuthFetch } from "@/lib/auth/auth-fetch";

import type { components, paths } from "./schema";

export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? "";

const auth = createAuthFetch({ baseUrl: API_BASE_URL });

export const api = createClient<paths>({
  baseUrl: API_BASE_URL,
  credentials: "include",
  fetch: auth.authFetch,
});

/** Exchanges the refresh cookie for a new access token; false when there is no valid session. */
export const refreshSession = auth.refresh;

/** Headers the API requires on the cookie-authenticated endpoints (refresh, logout). */
export const csrfHeaders = { [CSRF_HEADER]: CSRF_VALUE };

export type Schemas = components["schemas"];
export type Me = Schemas["MeResponse"];
