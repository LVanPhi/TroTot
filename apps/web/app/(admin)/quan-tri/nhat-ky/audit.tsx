"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { secondaryButton } from "@/components/domain/form-controls";
import { StatusBadge } from "@/components/domain/listing-status";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api/client";
import { formatDateTime } from "@/lib/format";

const PAGE_SIZE = 50;
const toIso = (d: string) => (d ? new Date(`${d}T00:00:00+07:00`).toISOString() : undefined);

/** US-805: every listing transition and every document opening, filtered by person and time. */
export function AuditAll() {
  const [tab, setTab] = useState<"transitions" | "documents">("transitions");
  const [person, setPerson] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [page, setPage] = useState(1);
  const personId = /^\d+$/.test(person) ? Number(person) : undefined;

  const transitions = useQuery({
    queryKey: ["admin", "audit", "transitions", personId, from, to, page],
    enabled: tab === "transitions",
    placeholderData: keepPreviousData,
    queryFn: async () =>
      (
        await api.GET("/api/v1/admin/audit/transitions", {
          params: { query: { actorId: personId, from: toIso(from), to: toIso(to), page, pageSize: PAGE_SIZE } },
        })
      ).data ?? null,
  });
  const documents = useQuery({
    queryKey: ["admin", "audit", "documents", personId, from, to, page],
    enabled: tab === "documents",
    placeholderData: keepPreviousData,
    queryFn: async () =>
      (
        await api.GET("/api/v1/admin/audit/document-access", {
          params: { query: { viewerId: personId, from: toIso(from), to: toIso(to), page, pageSize: PAGE_SIZE } },
        })
      ).data ?? null,
  });
  const total = Number((tab === "transitions" ? transitions.data?.total : documents.data?.total) ?? 0);

  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Nhật ký hệ thống</h1>
      <div role="group" aria-label="Loại nhật ký" className="flex gap-2">
        <button type="button" aria-pressed={tab === "transitions"} className={secondaryButton} onClick={() => { setTab("transitions"); setPage(1); }}>
          Chuyển trạng thái tin
        </button>
        <button type="button" aria-pressed={tab === "documents"} className={secondaryButton} onClick={() => { setTab("documents"); setPage(1); }}>
          Mở giấy tờ
        </button>
      </div>
      <div className="flex flex-wrap gap-3">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="person">{tab === "transitions" ? "Id người thao tác" : "Id người mở"}</Label>
          <Input id="person" inputMode="numeric" className="h-11" value={person} onChange={(e) => setPerson(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="a-from">Từ ngày</Label>
          <Input id="a-from" type="date" className="h-11" value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="a-to">Trước ngày</Label>
          <Input id="a-to" type="date" className="h-11" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
      </div>
      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border text-sm">
        {tab === "transitions"
          ? (transitions.data?.items ?? []).map((t) => (
              <li key={String(t.id)} className="flex flex-wrap items-center gap-2 p-3">
                <span className="text-muted-foreground">{formatDateTime(t.createdAt)}</span>
                <span className="font-medium">{t.listingTitle}</span>
                <StatusBadge status={t.toStatus} />
                <span>
                  bởi {t.actorName} (#{String(t.actorId)}){t.reason ? ` · ${t.reason}` : ""}
                </span>
              </li>
            ))
          : (documents.data?.items ?? []).map((d) => (
              <li key={String(d.id)} className="flex flex-wrap items-center gap-2 p-3">
                <span className="text-muted-foreground">{formatDateTime(d.accessedAt)}</span>
                <span>
                  {d.viewerName} (#{String(d.viewerId)}) mở giấy tờ #{String(d.documentId)} của #{String(d.ownerId)} · {d.purpose}
                </span>
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
