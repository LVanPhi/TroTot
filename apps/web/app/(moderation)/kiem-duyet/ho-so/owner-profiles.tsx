"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { OwnerPanel } from "@/components/domain/owner-panel";
import { api } from "@/lib/api/client";
import { formatDateTime } from "@/lib/format";

const KEY = ["moderation", "owner-profiles"] as const;

/** US-503 / US-605: profiles with pending documents first; review documents, open them with a purpose, verify. */
export function OwnerProfiles() {
  const [pending, setPending] = useState(true);
  const [selected, setSelected] = useState<number | null>(null);
  const list = useQuery({
    queryKey: [...KEY, pending],
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.GET("/api/v1/moderation/owner-profiles", { params: { query: { pending, pageSize: 50 } } })).data ?? null,
  });
  const detail = useQuery({
    queryKey: [...KEY, "detail", selected],
    enabled: selected !== null,
    queryFn: async () =>
      (await api.GET("/api/v1/moderation/owner-profiles/{userId}", { params: { path: { userId: selected ?? 0 } } })).data ?? null,
  });

  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Hồ sơ chủ</h1>
      <label className="flex min-h-11 items-center gap-2">
        <input type="checkbox" className="size-4" checked={pending} onChange={(e) => setPending(e.target.checked)} />
        Chỉ hồ sơ có giấy tờ chờ duyệt
      </label>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_2fr]">
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {(list.data?.items ?? []).map((p) => (
            <li key={String(p.userId)}>
              <button
                type="button"
                onClick={() => setSelected(Number(p.userId))}
                className={`flex w-full flex-col items-start gap-1 p-3 text-left hover:bg-muted ${selected === Number(p.userId) ? "bg-muted" : ""}`}
              >
                <span className="font-medium">{p.ownerType === "business" ? p.businessName : p.fullName}</span>
                <span className="text-sm text-muted-foreground">
                  {Number(p.pendingDocuments)} giấy tờ chờ ·{" "}
                  {p.verifiedUntil ? `xác minh đến ${formatDateTime(p.verifiedUntil)}` : "chưa xác minh"}
                </span>
              </button>
            </li>
          ))}
          {list.data && list.data.items.length === 0 ? <li className="p-3 text-muted-foreground">Không có hồ sơ.</li> : null}
        </ul>
        {detail.data ? (
          <section className="rounded-xl border border-border p-4">
            <OwnerPanel
              invalidate={KEY}
              data={{
                userId: detail.data.userId,
                fullName: detail.data.fullName,
                ownerType: detail.data.ownerType,
                businessName: detail.data.businessName,
                taxCode: detail.data.taxCode,
                verifiedUntil: detail.data.verifiedUntil,
                documents: detail.data.documents,
              }}
            />
          </section>
        ) : (
          <p className="text-muted-foreground">Chọn một hồ sơ để xem.</p>
        )}
      </div>
    </main>
  );
}
