"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { api } from "@/lib/api/client";
import { toProblem, type Problem } from "@/lib/api/problem";
import { watchScan } from "@/lib/realtime/scan-hub";

import { isFinal, type Scan } from "./view";

export const POLL_MS = 3000;
export const scanQueryKey = (scanId: string) => ["scan", scanId] as const;

export class ScanLoadError extends Error {
  constructor(readonly problem: Problem) {
    super(problem.code);
  }
}

/**
 * Poll interval for the result query (docs/06-frontend.md, mục 1): none once final, none while the hub is
 * watching, every 3 s otherwise.
 */
export function pollInterval(scan: Scan | undefined, hubConnected: boolean): number | false {
  if (scan && isFinal(scan.status)) return false;
  return hubConnected ? false : POLL_MS;
}

/** GET /scans/{id} kept fresh by the hub, with polling while the hub is unavailable. */
export function useScan(scanId: string) {
  const queryClient = useQueryClient();
  const [hubConnected, setHubConnected] = useState(false);

  const query = useQuery({
    queryKey: scanQueryKey(scanId),
    queryFn: async (): Promise<Scan> => {
      const result = await api.GET("/api/v1/scans/{scanId}", { params: { path: { scanId } } });
      if (!result.response.ok || !result.data) {
        const problem = toProblem(result.error, result.response.status);
        throw new ScanLoadError({ ...problem, status: problem.status ?? result.response.status });
      }
      return result.data;
    },
    retry: (count, error) => !(error instanceof ScanLoadError && error.problem.status === 404) && count < 2,
    refetchInterval: (q) => pollInterval(q.state.data, hubConnected),
  });

  const final = query.data ? isFinal(query.data.status) : false;
  useEffect(() => {
    if (final) return;
    const watch = watchScan(
      scanId,
      () => void queryClient.invalidateQueries({ queryKey: scanQueryKey(scanId) }),
      (connected) => {
        setHubConnected(connected);
        // Something may have finished while we were not watching.
        if (connected) void queryClient.invalidateQueries({ queryKey: scanQueryKey(scanId) });
      },
    );
    return () => void watch.stop();
  }, [scanId, final, queryClient]);

  return query;
}
