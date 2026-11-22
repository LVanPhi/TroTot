"use client";

import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";

import { secondaryButton } from "@/components/domain/form-controls";
import { RiskBadge } from "@/components/domain/scan-result-parts";
import { api } from "@/lib/api/client";
import { formatRelative } from "@/lib/format";
import { connectModeration } from "@/lib/realtime/moderation-hub";

const PAGE_SIZE = 20;
export const QUEUE_KEY = ["moderation", "queue"] as const;

/** US-601: highest risk first, AI failures on top, waiting time; refreshed by QueueChanged. */
export function Queue() {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const queue = useQuery({
    queryKey: [...QUEUE_KEY, page],
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.GET("/api/v1/moderation/queue", { params: { query: { page, pageSize: PAGE_SIZE } } })).data ?? null,
  });

  useEffect(() => {
    const hub = connectModeration({
      onQueueChanged: () => void queryClient.invalidateQueries({ queryKey: QUEUE_KEY }),
      onListingUpdated: () => void queryClient.invalidateQueries({ queryKey: QUEUE_KEY }),
    });
    return () => void hub.stop();
  }, [queryClient]);

  const total = Number(queue.data?.total ?? 0);
  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Hàng đợi duyệt</h1>
      <p className="text-sm text-muted-foreground" aria-live="polite">
        {queue.data ? `${total} tin chờ duyệt` : "Đang tải…"}
      </p>
      {queue.data && queue.data.items.length === 0 ? <p>Không còn tin nào chờ duyệt.</p> : null}
      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {(queue.data?.items ?? []).map((item) => (
          <li key={item.listingId}>
            <Link href={`/kiem-duyet/tin/${item.listingId}`} className="flex min-h-11 flex-wrap items-center justify-between gap-2 p-3 hover:bg-muted">
              <span className="flex min-w-0 flex-col">
                <span className="truncate font-medium">{item.title}</span>
                <span className="text-sm text-muted-foreground">
                  {item.listingType === "job" ? "Việc làm" : "Phòng trọ"} · chờ {formatRelative(item.queuedAt).replace(" trước", "")}
                </span>
              </span>
              <span className="flex items-center gap-2">
                {item.riskLevel && item.score != null ? (
                  <>
                    <RiskBadge level={item.riskLevel} size="sm" />
                    <span className="tabular-nums">{Number(item.score)}/100</span>
                    <span className="text-sm text-muted-foreground">{Number(item.flagCount)} dấu hiệu</span>
                  </>
                ) : (
                  <span className="inline-flex items-center gap-1 text-sm font-medium text-amber-900">
                    <CircleAlert aria-hidden className="size-4" />
                    Chưa có điểm AI, cần xem tay
                  </span>
                )}
              </span>
            </Link>
          </li>
        ))}
      </ul>
      {total > PAGE_SIZE ? (
        <nav aria-label="Phân trang" className="flex justify-between">
          <button type="button" className={secondaryButton} disabled={page <= 1} onClick={() => setPage(page - 1)}>
            Trang trước
          </button>
          <button type="button" className={secondaryButton} disabled={page * PAGE_SIZE >= total} onClick={() => setPage(page + 1)}>
            Trang sau
          </button>
        </nav>
      ) : null}
    </main>
  );
}
