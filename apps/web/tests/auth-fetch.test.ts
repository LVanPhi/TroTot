import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { createAuthFetch } from "@/lib/auth/auth-fetch";
import { session } from "@/lib/auth/session";

const BASE = "http://api.test";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const unauthenticated = () => json(401, { code: "auth.unauthenticated" });

describe("createAuthFetch", () => {
  beforeEach(() => session.reset());
  afterEach(() => session.reset());

  it("attaches the in-memory access token", async () => {
    session.signIn("token-1");
    const inner = vi.fn<(request: Request) => Promise<Response>>(async () => json(200, {}));

    await createAuthFetch({ baseUrl: BASE, fetch: inner }).authFetch(new Request(`${BASE}/api/v1/me`));

    expect(inner.mock.calls[0][0].headers.get("Authorization")).toBe("Bearer token-1");
  });

  it("on 401 refreshes once, then retries the request with the new token", async () => {
    session.signIn("old");
    const inner = vi.fn(async (request: Request) => {
      if (request.url.endsWith("/auth/refresh")) return json(200, { accessToken: "new", expiresIn: 900 });
      return request.headers.get("Authorization") === "Bearer new" ? json(200, { ok: true }) : unauthenticated();
    });

    const response = await createAuthFetch({ baseUrl: BASE, fetch: inner }).authFetch(
      new Request(`${BASE}/api/v1/me`, { method: "PATCH", body: JSON.stringify({ fullName: "A" }) }),
    );

    expect(response.status).toBe(200);
    expect(session.accessToken()).toBe("new");
    const refresh = inner.mock.calls.map(([r]) => r).find((r) => r.url.endsWith("/auth/refresh"))!;
    expect(refresh.method).toBe("POST");
    expect(refresh.headers.get("X-Requested-With")).toBe("troviec");
    const retried = inner.mock.calls.at(-1)![0];
    expect(await retried.text()).toBe(JSON.stringify({ fullName: "A" }));
  });

  it("parallel 401s share a single refresh", async () => {
    session.signIn("old");
    let refreshes = 0;
    const inner = vi.fn(async (request: Request) => {
      if (request.url.endsWith("/auth/refresh")) {
        refreshes++;
        await new Promise((resolve) => setTimeout(resolve, 10));
        return json(200, { accessToken: "new" });
      }
      return request.headers.get("Authorization") === "Bearer new" ? json(200, {}) : unauthenticated();
    });
    const { authFetch } = createAuthFetch({ baseUrl: BASE, fetch: inner });

    const responses = await Promise.all([1, 2, 3].map(() => authFetch(new Request(`${BASE}/api/v1/me`))));

    expect(responses.map((r) => r.status)).toEqual([200, 200, 200]);
    expect(refreshes).toBe(1);
  });

  it("when refresh fails, signs out and returns the original 401 without retrying again", async () => {
    session.signIn("old");
    const inner = vi.fn(async (request: Request) =>
      request.url.endsWith("/auth/refresh") ? json(401, { code: "auth.refresh_invalid" }) : unauthenticated(),
    );

    const response = await createAuthFetch({ baseUrl: BASE, fetch: inner }).authFetch(new Request(`${BASE}/api/v1/me`));

    expect(response.status).toBe(401);
    expect(session.get()).toEqual({ status: "guest", accessToken: null });
    expect(inner).toHaveBeenCalledTimes(2); // request + refresh, no second retry
  });

  it("does not refresh for other 401s (e.g. wrong password) or for guests", async () => {
    session.signIn("t");
    const inner = vi.fn(async () => json(401, { code: "auth.invalid_credentials" }));
    await createAuthFetch({ baseUrl: BASE, fetch: inner }).authFetch(new Request(`${BASE}/api/v1/auth/login`, { method: "POST" }));
    expect(inner).toHaveBeenCalledTimes(1);

    session.signOut();
    const guest = vi.fn(async () => unauthenticated());
    await createAuthFetch({ baseUrl: BASE, fetch: guest }).authFetch(new Request(`${BASE}/api/v1/me`));
    expect(guest).toHaveBeenCalledTimes(1);
  });

  it("never touches web storage", async () => {
    const setItem = vi.fn();
    vi.stubGlobal("localStorage", { setItem, getItem: vi.fn(), removeItem: vi.fn() });
    vi.stubGlobal("sessionStorage", { setItem, getItem: vi.fn(), removeItem: vi.fn() });
    const inner = vi.fn(async (request: Request) =>
      request.url.endsWith("/auth/refresh") ? json(200, { accessToken: "new" }) : json(200, {}),
    );

    await createAuthFetch({ baseUrl: BASE, fetch: inner }).refresh();
    session.signIn("x");
    session.signOut();

    expect(setItem).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });
});
