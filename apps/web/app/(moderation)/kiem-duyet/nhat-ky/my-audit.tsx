"use client";

import { useQuery } from "@tanstack/react-query";

import { api } from "@/lib/api/client";
import { formatDateTime } from "@/lib/format";
import { AUDIT_KIND_LABELS } from "@/lib/moderation/labels";

/** US-609: my decisions and document openings, newest first. */
export function MyAudit() {
  const audit = useQuery({
    queryKey: ["moderation", "audit", "me"],
    queryFn: async () => (await api.GET("/api/v1/moderation/audit/me")).data ?? [],
  });
  return (
    <main className="flex flex-col gap-4">
      <h1 className="text-2xl font-semibold tracking-tight">Nhật ký của tôi</h1>
      {audit.isPending ? <p aria-busy="true">Đang tải…</p> : null}
      {audit.data && audit.data.length === 0 ? <p>Chưa có thao tác nào.</p> : null}
      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {(audit.data ?? []).map((a, i) => (
          <li key={`${a.at}-${i}`} className="flex flex-col gap-1 p-3">
            <span className="font-medium">
              {AUDIT_KIND_LABELS[a.kind] ?? a.kind} · {formatDateTime(a.at)}
            </span>
            <span className="text-sm text-muted-foreground">
              {a.target}
              {a.detail ? ` · ${a.detail}` : ""}
            </span>
          </li>
        ))}
      </ul>
    </main>
  );
}
