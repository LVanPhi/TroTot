import { session } from "./session";

export const CSRF_HEADER = "X-Requested-With";
export const CSRF_VALUE = "troviec";

type FetchLike = (input: Request) => Promise<Response>;

interface AuthFetchOptions {
  baseUrl: string;
  fetch?: FetchLike;
}

/**
 * fetch used by the API client:
 *  - attaches the in-memory access token;
 *  - on 401 auth.unauthenticated, refreshes once (one shared promise for parallel requests) and retries once;
 *  - if the refresh fails, the session ends (guest) and the original 401 is returned.
 */
export function createAuthFetch({ baseUrl, fetch: inner = (r) => fetch(r) }: AuthFetchOptions) {
  let refreshing: Promise<boolean> | null = null;

  async function refresh(): Promise<boolean> {
    refreshing ??= (async () => {
      try {
        const response = await inner(
          new Request(`${baseUrl}/api/v1/auth/refresh`, {
            method: "POST",
            credentials: "include",
            headers: { [CSRF_HEADER]: CSRF_VALUE },
          }),
        );
        if (!response.ok) {
          session.signOut();
          return false;
        }
        const body = (await response.json()) as { accessToken: string };
        session.signIn(body.accessToken);
        return true;
      } catch {
        session.signOut();
        return false;
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  }

  async function send(request: Request): Promise<Response> {
    const token = session.accessToken();
    const authed = new Request(request, { credentials: "include" });
    if (token) authed.headers.set("Authorization", `Bearer ${token}`);
    return inner(authed);
  }

  async function authFetch(request: Request): Promise<Response> {
    const retryCopy = request.clone();
    const response = await send(request);
    if (response.status !== 401 || !(await isUnauthenticated(response))) return response;
    if (!session.accessToken()) return response; // guest: nothing to refresh
    if (!(await refresh())) return response;
    return send(retryCopy);
  }

  return { authFetch, refresh };
}

async function isUnauthenticated(response: Response): Promise<boolean> {
  try {
    const body = (await response.clone().json()) as { code?: string };
    return body.code === "auth.unauthenticated";
  } catch {
    return false;
  }
}
