/**
 * In-memory session (docs/06-frontend.md, Phiên đăng nhập). The access token lives only in this module:
 * never localStorage, sessionStorage or a JS cookie. A reload starts as "unknown" until the refresh cookie
 * (HttpOnly, set by the API) is exchanged for a new access token.
 */
export type SessionStatus = "unknown" | "guest" | "signed_in";

export interface SessionSnapshot {
  status: SessionStatus;
  accessToken: string | null;
}

type Listener = () => void;

let snapshot: SessionSnapshot = { status: "unknown", accessToken: null };
const listeners = new Set<Listener>();

function emit(next: SessionSnapshot) {
  snapshot = next;
  listeners.forEach((listener) => listener());
}

export const session = {
  get: (): SessionSnapshot => snapshot,
  accessToken: (): string | null => snapshot.accessToken,
  signIn: (accessToken: string) => emit({ status: "signed_in", accessToken }),
  signOut: () => emit({ status: "guest", accessToken: null }),
  subscribe(listener: Listener): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  /** Test helper: back to the state of a fresh page load. */
  reset: () => emit({ status: "unknown", accessToken: null }),
};
