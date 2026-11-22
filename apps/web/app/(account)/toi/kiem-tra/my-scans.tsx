"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";

import { linkClass, secondaryButton } from "@/components/domain/form-controls";
import { RiskBadge } from "@/components/domain/scan-result-parts";
import { api } from "@/lib/api/client";
import { formatDateTime } from "@/lib/format";
import { num } from "@/lib/scans/view";

const PAGE_SIZE = 20;

const statusText: Record<string, string> = {
  queued: "Đang chờ",
  processing: "Đang phân tích",
  failed: "Chưa phân tích được",
};

export function MyScans() {
  const [page, setPage] = useState(1);
  const { data, isPending, isError } = useQuery({
    queryKey: ["me", "scans", page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET("/api/v1/me/scans", { params: { query: { page, pageSize: PAGE_SIZE } } });
      if (error || !data) throw new Error("me.scans");
      return data;
    },
  });

  if (isPending) return <p aria-busy="true">Đang tải…</p>;
  if (isError) return <p role="alert">Chưa tải được lịch sử, vui lòng thử lại.</p>;
  if (data.items.length === 0)
    return (
      <p>
        Bạn chưa kiểm tra tin nào.{" "}
        <Link href="/kiem-tra" className={linkClass}>
          Kiểm tra một tin
        </Link>
      </p>
    );

  const pages = Math.max(1, Math.ceil(num(data.total) / PAGE_SIZE));
  return (
    <div className="flex flex-col gap-4">
      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {data.items.map((s) => (
          <li key={s.scanId}>
            <Link href={`/kiem-tra/${s.scanId}`} className="flex min-h-11 flex-wrap items-center justify-between gap-2 p-4 hover:bg-muted">
              <span>{formatDateTime(s.createdAt)}</span>
              {s.riskLevel && s.score !== null ? (
                <span className="flex items-center gap-2">
                  <RiskBadge level={s.riskLevel} size="sm" />
                  <span className="tabular-nums">{num(s.score)}/100</span>
                </span>
              ) : (
                <span className="text-sm text-muted-foreground">{statusText[s.status] ?? s.status}</span>
              )}
            </Link>
          </li>
        ))}
      </ul>
      {pages > 1 ? (
        <nav aria-label="Phân trang" className="flex items-center justify-between gap-2">
          <button type="button" className={secondaryButton} disabled={page <= 1} onClick={() => setPage(page - 1)}>
            Trang trước
          </button>
          <span className="text-sm">
            Trang {page}/{pages}
          </span>
          <button type="button" className={secondaryButton} disabled={page >= pages} onClick={() => setPage(page + 1)}>
            Trang sau
          </button>
        </nav>
      ) : null}
    </div>
  );
}
