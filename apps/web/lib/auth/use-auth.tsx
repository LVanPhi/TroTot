"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useSyncExternalStore, type ReactNode } from "react";

import { api, csrfHeaders, refreshSession, type Me } from "@/lib/api/client";

import { session, type SessionSnapshot } from "./session";

export const ME_QUERY_KEY = ["me"] as const;

export function useSession(): SessionSnapshot {
  return useSyncExternalStore(session.subscribe, session.get, session.get);
}

/** Restores the session once per page load by exchanging the HttpOnly refresh cookie. */
export function AuthProvider({ children }: { children: ReactNode }) {
  useEffect(() => {
    if (session.get().status === "unknown") void refreshSession();
  }, []);
  return <>{children}</>;
}

/** The signed-in account (GET /me), or undefined for guests and while loading. */
export function useMe() {
  const { status } = useSession();
  return useQuery({
    queryKey: ME_QUERY_KEY,
    enabled: status === "signed_in",
    queryFn: async (): Promise<Me> => {
      const { data, error } = await api.GET("/api/v1/me");
      if (error || !data) throw new Error("me");
      return data;
    },
  });
}

/** True when the signed-in account has the permission. Pages gate by permission, never by role name. */
export function can(me: Me | undefined, permission: string): boolean {
  return me?.permissions.includes(permission) ?? false;
}

export function useSignOut() {
  const queryClient = useQueryClient();
  return async () => {
    await api.POST("/api/v1/auth/logout", { headers: csrfHeaders });
    session.signOut();
    queryClient.removeQueries({ queryKey: ME_QUERY_KEY });
  };
}
