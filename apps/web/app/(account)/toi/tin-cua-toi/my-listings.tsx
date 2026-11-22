"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import Link from "next/link";
import { useState } from "react";

import { linkClass, secondaryButton } from "@/components/domain/form-controls";
import { StatusBadge } from "@/components/domain/listing-status";
import { api } from "@/lib/api/client";
import { formatDateTime } from "@/lib/format";
import { STATUS_LABELS, type ListingStatus } from "@/lib/listings/model";
import { num } from "@/lib/scans/view";

const FILTERS: (ListingStatus | "all")[] = ["all", "draft", "screening", "pending_review", "verified", "rejected", "hidden", "expired", "removed"];
const PAGE_SIZE = 20;

export const myListingsKey = (status: string, page: number) => ["me", "listings", status, page] as const;

export function MyListings() {
  const [status, setStatus] = useState<ListingStatus | "all">("all");
  const [page, setPage] = useState(1);
  const { data, isPending, isError } = useQuery({
    queryKey: myListingsKey(status, page),
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET("/api/v1/me/listings", {
        params: { query: { status: status === "all" ? undefined : status, page, pageSize: PAGE_SIZE } },
      });
      if (error || !data) throw new Error("me.listings");
      return data;
    },
  });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">Tin của tôi</h1>
        <Link href="/toi/tin-cua-toi/moi" className={`${secondaryButton} gap-2`}>
          <Plus aria-hidden className="size-4" />
          Đăng tin mới
        </Link>
      </div>
      <div role="group" aria-label="Lọc theo trạng thái" className="flex flex-wrap gap-2">
        {FILTERS.map((f) => (
          <button
            key={f}
            type="button"
            aria-pressed={status === f}
            onClick={() => {
              setStatus(f);
              setPage(1);
            }}
            className={`min-h-11 rounded-full border px-3 text-sm ${status === f ? "border-primary bg-primary text-primary-foreground" : "border-border"}`}
          >
            {f === "all" ? "Tất cả" : STATUS_LABELS[f]}
          </button>
        ))}
      </div>
      {isPending ? <p aria-busy="true">Đang tải…</p> : null}
      {isError ? <p role="alert">Chưa tải được danh sách, vui lòng thử lại.</p> : null}
      {data && data.items.length === 0 ? (
        <p>
          Chưa có tin nào.{" "}
          <Link href="/toi/tin-cua-toi/moi" className={linkClass}>
            Đăng tin đầu tiên
          </Link>
        </p>
      ) : null}
      {data && data.items.length > 0 ? (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {data.items.map((l) => (
            <li key={l.listingId}>
              <Link href={`/toi/tin-cua-toi/${l.listingId}`} className="flex min-h-11 items-center gap-3 p-3 hover:bg-muted">
                {l.coverImageUrl ? (
                  // eslint-disable-next-line @next/next/no-img-element -- listing photo served by Caddy
                  <img src={l.coverImageUrl} alt="" width={64} height={64} className="size-16 rounded-md object-cover" />
                ) : (
                  <div aria-hidden className="size-16 rounded-md bg-muted" />
                )}
                <div className="flex min-w-0 flex-1 flex-col gap-1">
                  <span className="truncate font-medium">{l.title}</span>
                  <span className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
                    <StatusBadge status={l.status} />
                    Cập nhật {formatDateTime(l.updatedAt)}
                  </span>
                </div>
              </Link>
            </li>
          ))}
        </ul>
      ) : null}
      {data && num(data.total) > PAGE_SIZE ? (
        <nav aria-label="Phân trang" className="flex items-center justify-between">
          <button type="button" className={secondaryButton} disabled={page <= 1} onClick={() => setPage(page - 1)}>
            Trang trước
          </button>
          <button type="button" className={secondaryButton} disabled={page * PAGE_SIZE >= num(data.total)} onClick={() => setPage(page + 1)}>
            Trang sau
          </button>
        </nav>
      ) : null}
    </div>
  );
}
